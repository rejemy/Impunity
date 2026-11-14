using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Dreamwing.Cons
{
	public class Cons : MonoBehaviour, IScrollHandler
	{
		public const string WarningColor = "<color=#FFFF66>";
		public const string ErrorColor = "<color=#FF6666>";
		public const string ExceptionColor = "<color=#FF6666>";
		public const string CommandColor = "<color=#AAAAAA>";
		private const string ColorEndTag = "</color>";

		private static string EarlyConsoleOutput = "";
		internal static Cons Singleton;

		public static Action OnOpened;
		public static Action OnClosed;

		public Text ConsoleOutput;
		public ConsoleInputField ConsoleInput;
		public ScrollRect ConsoleScroll;

		private RectTransform ConsoleTransform;

		private static Commander Cmds;


		private bool _IsOpen;

		// Slide in/out easing
		private float StartHeight;
		private float DestinationHeight;
		private float StartTime = 0.0f;
		private float DestinationTime = 0.0f;
		public static float OpenCloseTime = 0.2f;

		// Output appended since the last LateUpdate, measured there to hold a
		// scrolled-back view steady as new lines arrive
		private readonly StringBuilder AppendedSinceLastFrame = new StringBuilder();
		// Private generator for measuring output, so the output text's own cached
		// layout isn't disturbed
		private readonly TextGenerator TextMeasure = new TextGenerator();

		// Content y within this of 0 counts as pinned to the newest output
		private const float AtBottomTolerance = 1.0f;

		private static Thread MainThread;
		private static readonly object BackgroundThreadLock = new object();
		private static string BackgroundThreadBuffer = null;

		// When set, console output is routed here instead of to the on-screen panel.
		// RunCommandCapturingOutput uses this to capture a command's output silently (run a
		// command, collect what it logs, restore). Main thread only.
		// Its second argument says whether the line is an error (or exception).
		internal static Action<string, bool> OutputRedirect;

		// Safe to call more than once (a scene's own bootstrap and DevTools may both): only the
		// first call sets up, so commands registered in between survive.
		public static void Init()
		{
			if (Cmds != null)
				return;

			MainThread = Thread.CurrentThread;
			UnityDebugInterceptor.Init();
			Cmds = new Commander();
			BuiltinCommands.Init();
		}

		public static void AddCommandGroup(string group, string helpText)
		{
			Cmds.AddCommandGroup(group, helpText);
		}

		public static void AddCommand(string command, Action action, string helpText)
		{
			Cmds.AddCommand(command, action, helpText, null);
		}

		public static void AddCommand<T>(string command, Action<T> action, string helpText,
			CommandArgument arg1)
		{
			CommandArgument[] arguments = new CommandArgument[] { arg1 };
			Cmds.AddCommand(command, action, helpText, arguments);
		}

		public static void AddCommand<T1, T2>(string command, Action<T1, T2> action, string helpText,
			CommandArgument arg1, CommandArgument arg2)
		{
			CommandArgument[] arguments = new CommandArgument[] { arg1, arg2 };
			Cmds.AddCommand(command, action, helpText, arguments);
		}

		public static void AddCommand<T1, T2, T3>(string command, Action<T1, T2, T3> action, string helpText,
			CommandArgument arg1, CommandArgument arg2, CommandArgument arg3)
		{
			CommandArgument[] arguments = new CommandArgument[] { arg1, arg2, arg3 };
			Cmds.AddCommand(command, action, helpText, arguments);
		}

		public static void AddCommand<T1, T2, T3, T4>(string command, Action<T1, T2, T3, T4> action, string helpText,
			CommandArgument arg1, CommandArgument arg2, CommandArgument arg3, CommandArgument arg4)
		{
			CommandArgument[] arguments = new CommandArgument[] { arg1, arg2, arg3, arg4 };
			Cmds.AddCommand(command, action, helpText, arguments);
		}

		public static CommandNode GetCommandInfo(string commandPath)
		{
			return Cmds.GetCommandInfo(commandPath);
		}

		// help [path...] / help --all [path...] / help --find <text>
		public static void Help(string[] args)
		{
			Cmds.Help(args);
		}

		// False when the line didn't run cleanly (see Commander.Process)
		public static bool RunCommand(string command)
		{
			return Cmds.Process(command);
		}

		// Runs a command and returns what it logged (lines joined with '\n') instead of showing
		// it on the panel. Main thread only. Used by the dev tooling commands that drive the
		// console from outside the game (see docs/AgentTooling.md).
		//
		// failed: the line didn't run cleanly, or something logged an error or exception while it
		// ran — how console commands refuse ("game new: already in a game"). Only what happens
		// synchronously counts; errors from work the command started for later frames don't.
		public static string RunCommandCapturingOutput(string command, out bool failed)
		{
			Capture capture = RunCaptured(() => RunCommand(command), out bool ran);
			// Replies the command deferred finish on the panel: nobody here waits for them
			capture.ReleaseReplies();
			failed = !ran || capture.LoggedError;
			return capture.Output.ToString();
		}

		// RunCommandCapturingOutput for commands that answer on a later frame (Defer): also
		// waits for every reply the command deferred, up to timeoutSeconds, capturing what they
		// log. A reply still open at the timeout is let go (its later output goes to the panel)
		// and counts as a failure. Main thread only; the await resumes on it.
		public static Task<(string output, bool failed)> RunCommandCapturingOutputAsync(string command, float timeoutSeconds)
		{
			return RunCapturingOutputAsync(() => RunCommand(command), timeoutSeconds);
		}

		// The same around any code standing in for a command line (run returns whether it ran
		// cleanly); the seam the unit tests use, as they have no command tree.
		public static async Task<(string output, bool failed)> RunCapturingOutputAsync(Func<bool> run, float timeoutSeconds)
		{
			Capture capture = RunCaptured(run, out bool ran);
			bool timedOut = false;
			if (capture.Replies.Count > 0)
			{
				Task all = Task.WhenAll(capture.Replies.ConvertAll(r => r.Finished.Task));
				Task first = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
				timedOut = first != all;
			}
			capture.ReleaseReplies();
			if (timedOut)
			{
				capture.Append(ErrorColor + "(timed out after " + timeoutSeconds + "s waiting for the command to finish)" + ColorEndTag, true);
			}
			return (capture.Output.ToString(), !ran || capture.LoggedError);
		}

		// Called by a command whose answer comes on a later frame (a server round trip): log
		// through the reply, then Finish it. While a captured run is waiting for it
		// (RunCommandCapturingOutputAsync, how `bv console` / `tools/ucmd game` run lines) its
		// lines join that run's output; otherwise they go to the panel like any Cons.Log.
		// Main thread only.
		public static ConsoleReply Defer()
		{
			ConsoleReply reply = new ConsoleReply();
			if (ActiveCapture != null)
			{
				reply.Sink = ActiveCapture.Append;
				ActiveCapture.Replies.Add(reply);
			}
			return reply;
		}

		// The command being run under capture right now, for Defer to join
		private static Capture ActiveCapture;

		private static Capture RunCaptured(Func<bool> run, out bool ran)
		{
			Capture capture = new Capture();
			Action<string, bool> previous = OutputRedirect;
			Capture previousCapture = ActiveCapture;
			OutputRedirect = capture.Append;
			ActiveCapture = capture;
			try
			{
				ran = run();
			}
			finally
			{
				OutputRedirect = previous;
				ActiveCapture = previousCapture;
			}
			return capture;
		}

		private sealed class Capture
		{
			public readonly StringBuilder Output = new StringBuilder();
			public readonly List<ConsoleReply> Replies = new List<ConsoleReply>();
			public bool LoggedError;

			public void Append(string text, bool isError)
			{
				if (Output.Length > 0)
					Output.Append('\n');
				Output.Append(text);
				LoggedError |= isError;
			}

			public void ReleaseReplies()
			{
				foreach (ConsoleReply reply in Replies)
					reply.Sink = null;
			}
		}

		// Formats a line the way LogMessage does; for ConsoleReply
		internal static void WriteReplyLine(Action<string, bool> sink, LogType logType, string message)
		{
			if (sink == null)
			{
				LogMessage(logType, message);
				return;
			}
			bool isError = logType == LogType.Error || logType == LogType.Exception;
			if (logType == LogType.Warning)
				message = WarningColor + message + ColorEndTag;
			else if (isError)
				message = ErrorColor + message + ColorEndTag;
			sink(message, isError);
		}

		void Awake()
		{
			Singleton = this;

			ConsoleTransform = transform as RectTransform;
			//ConsoleTransform.sizeDelta = new Vector2(0.0f, Screen.height);
			ConsoleTransform.anchoredPosition = new Vector2(0.0f, Screen.height);
		}

		void Start()
		{
			string earlyOutput;
			lock (BackgroundThreadLock)
			{
				earlyOutput = EarlyConsoleOutput;
				EarlyConsoleOutput = null;
			}

			if (earlyOutput.Length > 0)
				AppendToConsoleOutput(earlyOutput);

			ConsoleInput.OnCommand = ProcessCommand;
		}

		private void ProcessCommand(string commandLine)
		{
			string echo = CommandColor + commandLine + ColorEndTag;
			LogText(echo);

			Cmds.Process(commandLine);
		}


		private void OnTab()
		{
			CommandCompletion completion = Cmds.Complete(ConsoleInput.text);
			if (completion.Candidates != null)
				LogColumns(completion.Candidates, CommandColor);

			ConsoleInput.text = completion.Line;
			ConsoleInput.caretPosition = ConsoleInput.text.Length;
		}

		public static void Log(string message)
		{
			LogMessage(LogType.Log, message);
		}

		public static void LogWarning(string message)
		{
			LogMessage(LogType.Warning, message);
		}

		public static void LogError(string message)
		{
			LogMessage(LogType.Error, message);
		}

		public static void LogMessage(LogType logType, string message)
		{
			string startTag = null;
			switch (logType)
			{
				case LogType.Warning:
					startTag = WarningColor;
					break;
				case LogType.Error:
					startTag = ErrorColor;
					break;
				case LogType.Exception:
					startTag = ExceptionColor;
					break;
			}

			if (startTag != null)
				message = startTag + message + ColorEndTag;

			LogText(message, logType == LogType.Error || logType == LogType.Exception || logType == LogType.Assert);
		}

		public static void LogException(Exception exception)
		{
			LogText(ExceptionColor + exception.ToString() + ColorEndTag, true);
		}

		private static void LogText(string text, bool isError = false)
		{
			// Output capture: swallow the line from the panel and hand it to the
			// redirect sink instead. Main-thread only so background logs racing during
			// a capture window fall through to normal panel handling.
			if (OutputRedirect != null && Thread.CurrentThread == MainThread)
			{
				OutputRedirect(text, isError);
				return;
			}

			// Until the panel exists, hold output for it. Release builds never create
			// the panel, so this is capped like the panel itself or it grows forever.
			// Checked under the lock: Start hands the buffer over from the main thread
			// while background threads may be logging.
			lock (BackgroundThreadLock)
			{
				if (EarlyConsoleOutput != null)
				{
					EarlyConsoleOutput = EarlyConsoleOutput.Length > 0
						? TrimToMaxChars(EarlyConsoleOutput + "\n" + text)
						: text;
					return;
				}
			}

			// No panel (never created, or gone): nowhere to show it
			Singleton?.AppendOutputText(text);
		}

		private void AppendOutputText(string text)
		{
			if (Thread.CurrentThread != MainThread)
			{
				lock (BackgroundThreadLock)
				{
					if (BackgroundThreadBuffer == null)
					{
						BackgroundThreadBuffer = text;
					}
					else
					{
						BackgroundThreadBuffer = TrimToMaxChars(BackgroundThreadBuffer + "\n" + text);
					}
				}
				return;
			}

			if (ConsoleOutput == null)
				return;

			AppendToConsoleOutput(text);
		}

		// uGUI Text tops out near ~65k mesh vertices (~16k visible glyphs); beyond
		// that it rebuilds slowly and logs overflow errors during layout. Keep the
		// on-screen buffer comfortably under that limit.
		private const int MaxConsoleOutputChars = 16000;

		// Appends a line to the on-screen console (prefixing a newline when there is
		// already output) and trims the buffer back under the character cap, dropping
		// the oldest lines so the most recent output is always kept.
		private void AppendToConsoleOutput(string text)
		{
			string combined = ConsoleOutput.text.Length > 0
				? ConsoleOutput.text + "\n" + text
				: text;

			ConsoleOutput.text = TrimToMaxChars(combined);

			if (AppendedSinceLastFrame.Length > 0)
				AppendedSinceLastFrame.Append('\n');
			AppendedSinceLastFrame.Append(text);
		}

		// Trims text to at most MaxConsoleOutputChars, cutting at the first newline at
		// or after the trim point so a partial line is never left dangling at the top.
		// Falls back to a hard cut when the surviving tail is one long line with no
		// newline to break on.
		private static string TrimToMaxChars(string text)
		{
			if (text.Length <= MaxConsoleOutputChars)
				return text;

			int cut = text.Length - MaxConsoleOutputChars;
			int newline = text.IndexOf('\n', cut);
			return newline >= 0 ? text.Substring(newline + 1) : text.Substring(cut);
		}

		public static void Clear()
		{
			if (Singleton.ConsoleOutput == null)
				return;

			Singleton.ConsoleOutput.text = "";
			Singleton.AppendedSinceLastFrame.Clear();
			Singleton.ScrollToBottom();
		}

		// The output text has no raycast target, so the wheel lands on the panel's
		// background image — forward it so the whole console scrolls
		public void OnScroll(PointerEventData eventData)
		{
			ConsoleScroll.OnScroll(eventData);
		}

		private void ScrollToBottom()
		{
			ConsoleScroll.StopMovement();
			RectTransform content = ConsoleScroll.content;
			content.anchoredPosition = new Vector2(content.anchoredPosition.x, 0.0f);
		}

		// Output content is bottom-pivoted, so y == 0 keeps the newest line in view
		// and new output pushes older lines up. When the reader has scrolled back,
		// push the content down by the appended height so their view stays put.
		// (Trimming drops lines off the top, which doesn't move the rest.)
		private void HoldScrollbackPosition()
		{
			if (AppendedSinceLastFrame.Length == 0)
				return;

			RectTransform content = ConsoleScroll.content;
			if (content.anchoredPosition.y < -AtBottomTolerance)
			{
				// Bring the content size up to date first, or the ScrollRect clamps
				// the shifted position against the stale, shorter content
				LayoutRebuilder.ForceRebuildLayoutImmediate(content);
				content.anchoredPosition -= new Vector2(0.0f, MeasureAppendedHeight(AppendedSinceLastFrame.ToString()));
			}

			AppendedSinceLastFrame.Clear();
		}

		// How much taller the output gets when text is appended after an existing
		// line. Measured as a difference rather than the text alone, since the first
		// line of a block isn't the same height as a following one.
		private float MeasureAppendedHeight(string text)
		{
			// Same settings Text.preferredHeight uses
			TextGenerationSettings settings = ConsoleOutput.GetGenerationSettings(
				new Vector2(ConsoleOutput.GetPixelAdjustedRect().size.x, 0.0f));
			float withText = TextMeasure.GetPreferredHeight(" \n" + text, settings);
			float without = TextMeasure.GetPreferredHeight(" ", settings);
			return (withText - without) / ConsoleOutput.pixelsPerUnit;
		}

		// Logs short items (command names, ids, ...) as a grid of columns sized to
		// the console's width, in the order given (read down each column).
		// Captured output goes to tools with their own tab stops, so it stays one
		// item per line.
		public static void LogColumns(IList<string> items, string colorTag = null)
		{
			bool canMeasure = Thread.CurrentThread == MainThread && Singleton != null && Singleton.ConsoleOutput != null;
			if (OutputRedirect != null || !canMeasure)
			{
				foreach (string item in items)
					LogText(Colored("  " + item, colorTag));
				return;
			}

			if (items.Count > 0)
				LogText(Colored(Singleton.FormatColumns(items), colorTag));
		}

		// Logs (name, text) rows as two lined-up columns (help listings). Captured
		// output is padded with spaces instead, for tools.
		public static void LogRows(IList<(string Name, string Text)> rows)
		{
			if (rows.Count == 0)
				return;

			bool canMeasure = Thread.CurrentThread == MainThread && Singleton != null && Singleton.ConsoleOutput != null;
			if (OutputRedirect != null || !canMeasure)
			{
				LogText(ColumnLayout.FormatRowsPlain(rows));
				return;
			}

			LogText(Singleton.FormatRows(rows));
		}

		private string FormatRows(IList<(string Name, string Text)> rows)
		{
			TextGenerationSettings settings = ConsoleOutput.GetGenerationSettings(Vector2.zero);
			settings.horizontalOverflow = HorizontalWrapMode.Overflow;
			float pixelsPerUnit = ConsoleOutput.pixelsPerUnit;
			Func<string, float> measure = text => TextMeasure.GetPreferredWidth(text, settings) / pixelsPerUnit;

			return ColumnLayout.FormatRows(rows, measure,
				tabWidth: measure("\t"),
				minGap: measure("  "));
		}

		private static string Colored(string text, string colorTag)
		{
			return colorTag == null ? text : colorTag + text + ColorEndTag;
		}

		private string FormatColumns(IList<string> items)
		{
			TextGenerationSettings settings = ConsoleOutput.GetGenerationSettings(Vector2.zero);
			settings.horizontalOverflow = HorizontalWrapMode.Overflow;
			float pixelsPerUnit = ConsoleOutput.pixelsPerUnit;
			Func<string, float> measure = text => TextMeasure.GetPreferredWidth(text, settings) / pixelsPerUnit;

			return ColumnLayout.Format(items, measure,
				tabWidth: measure("\t"),
				lineWidth: ConsoleOutput.GetPixelAdjustedRect().width,
				minGap: measure("  "));
		}

		public static void ClearHistory()
		{
			Singleton.ConsoleInput.ClearHistory();
		}

		public static bool IsOpen
		{
			get
			{
				return Singleton != null && Singleton._IsOpen;
			}
		}

		public static void Open()
		{
			Singleton.OpenConsole();
		}

		public static void Close()
		{
			Singleton.CloseConsole();
		}

		public static void Toggle()
		{
			if (IsOpen)
				Close();
			else
				Open();
		}

		private void OpenConsole()
		{
			if (_IsOpen)
				return;

			EventSystem.current.SetSelectedGameObject(ConsoleInput.gameObject);
			ConsoleInput.selectionFocusPosition = 0;
			ConsoleInput.selectionAnchorPosition = 0;
			ConsoleInput.caretPosition = ConsoleInput.text.Length;

			ScrollToBottom();

			_IsOpen = true;

			SlideTo(0.0f);

			try
			{
				OnOpened?.Invoke();
			}
			catch (Exception e)
			{
				Debug.LogError("Exception in OnOpened: " + e.Message);
			}
		}

		private void CloseConsole()
		{
			if (!_IsOpen)
				return;

			Singleton._IsOpen = false;

			EventSystem.current.SetSelectedGameObject(null);

			SlideTo(Screen.height);

			try
			{
				OnClosed?.Invoke();
			}
			catch (Exception e)
			{
				Debug.LogError("Exception in OnClosed: " + e.Message);
			}
		}

		private void ClearStrayBacktick()
		{
			if (ConsoleInput.text.Length > 0 && ConsoleInput.text[ConsoleInput.text.Length - 1] == '`')
				ConsoleInput.text = ConsoleInput.text.Substring(0, ConsoleInput.text.Length - 1);
		}

		private void SlideTo(float dest)
		{
			StartHeight = ConsoleTransform.anchoredPosition.y;
			DestinationHeight = dest;
			StartTime = Time.time;
			DestinationTime = StartTime + TransitionTime();
		}

		private float TransitionTime()
		{
			float delta = Mathf.Abs(ConsoleTransform.anchoredPosition.y - DestinationHeight);
			return (delta / Screen.height) * OpenCloseTime;
		}


		void Update()
		{
			if (BackgroundThreadBuffer != null)
			{
				lock (BackgroundThreadLock)
				{
					AppendToConsoleOutput(BackgroundThreadBuffer);
					BackgroundThreadBuffer = null;
				}
			}

			// Null on devices without a keyboard
			Keyboard keyboard = Keyboard.current;

			if (keyboard != null && keyboard.backquoteKey.wasPressedThisFrame)
			{
				Cons.Toggle();
				ClearStrayBacktick();
				return;
			}

			if (DestinationTime > 0.0f)
			{
				if (Time.time >= DestinationTime)
				{
					DestinationTime = 0.0f;
					ConsoleTransform.anchoredPosition = new Vector2(0.0f, DestinationHeight);
				}
				else
				{
					float p = (Time.time - StartTime) / (DestinationTime - StartTime);
					float r = Easers.Deccel(p);

					float height = (DestinationHeight - StartHeight) * r + StartHeight;
					ConsoleTransform.anchoredPosition = new Vector2(0.0f, height);
				}

			}

			if (!_IsOpen)
				return;

			if (!ConsoleInput.isFocused)
			{
				EventSystem.current.SetSelectedGameObject(ConsoleInput.gameObject);
			}

			if (keyboard == null)
				return;

			if (keyboard.escapeKey.wasPressedThisFrame)
			{
				Cons.Close();
				return;
			}

			if (keyboard.tabKey.wasPressedThisFrame)
				OnTab();

			if (keyboard.pageUpKey.wasPressedThisFrame)
				ScrollPage(1);
			else if (keyboard.pageDownKey.wasPressedThisFrame)
				ScrollPage(-1);
		}

		// Scrolls back (+1, older output) or forward (-1) by a viewport's height,
		// less a line so the edge line stays in view for context
		private void ScrollPage(int direction)
		{
			ConsoleScroll.StopMovement();

			RectTransform content = ConsoleScroll.content;
			float viewHeight = ConsoleScroll.viewport.rect.height;
			float lineHeight = MeasureAppendedHeight(" ");
			float page = Mathf.Max(viewHeight - lineHeight, lineHeight);

			// Content is bottom-pivoted: y == 0 shows the newest output, and scrolling
			// back moves it down, to at most the overflow past the viewport
			float furthestBack = -Mathf.Max(content.rect.height - viewHeight, 0.0f);
			float y = Mathf.Clamp(content.anchoredPosition.y - direction * page, furthestBack, 0.0f);
			content.anchoredPosition = new Vector2(content.anchoredPosition.x, y);
		}

		void LateUpdate()
		{
			HoldScrollbackPosition();
		}
	}

}
