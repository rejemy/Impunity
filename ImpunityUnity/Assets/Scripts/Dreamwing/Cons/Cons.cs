using System;
using System.Threading;
using System.Collections.Generic;

using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Dreamwing.Cons
{
	public class Cons : MonoBehaviour
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
		public Scrollbar ConsoleScrollbar;

		private RectTransform ConsoleTransform;

		private static Commander Cmds;


		private bool _IsOpen;

		// Slide in/out easing
		private float StartHeight;
		private float DestinationHeight;
		private float StartTime = 0.0f;
		private float DestinationTime = 0.0f;
		public static float OpenCloseTime = 0.2f;

		private static Thread MainThread;
		private static readonly object BackgroundThreadLock = new object();
		private static string BackgroundThreadBuffer = null;

		public static void Init()
		{
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

		public static void RunCommand(string command)
		{
			Cmds.Process(command);
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
			if (EarlyConsoleOutput.Length > 0)
			{
				if (ConsoleOutput.text.Length > 0)
					ConsoleOutput.text += "\n";

				ConsoleOutput.text += EarlyConsoleOutput;
			}
			EarlyConsoleOutput = null;

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
			string partialCommand = ConsoleInput.text.Trim();
			if (partialCommand.Length == 0)
				return;

			string[] partialPath = partialCommand.ToLower().Split(' ');

			List<string> possibleCompletions = Cmds.GetPossibleCommands(partialPath);
			if (possibleCompletions == null || possibleCompletions.Count == 0)
			{
				ConsoleInput.caretPosition = ConsoleInput.text.Length;
				return;
			}

			if (possibleCompletions.Count == 1)
			{
				// Replace last part with completion
				partialPath[partialPath.Length - 1] = possibleCompletions[0];
				string newCommand = String.Join(" ", partialPath) + " ";
				ConsoleInput.text = newCommand;
				ConsoleInput.caretPosition = ConsoleInput.text.Length;
			}
			else
			{
				// More than one possible match.
				string hint = "Could be: " + String.Join(" ", possibleCompletions);
				LogText(CommandColor + hint + ColorEndTag);
			}

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

			LogText(message);
		}

		public static void LogException(Exception exception)
		{
			LogText(ExceptionColor + exception.ToString() + ColorEndTag);
		}

		private static void LogText(string text)
		{
			if (EarlyConsoleOutput != null)
			{
				lock (BackgroundThreadLock)
				{
					if (EarlyConsoleOutput.Length > 0)
						EarlyConsoleOutput += "\n" + text;
					else
						EarlyConsoleOutput += text;
				}
			}
			else
			{
				Singleton.AppendOutputText(text);
			}
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
						BackgroundThreadBuffer += "\n" + text;
					}
				}
				return;
			}

			if (ConsoleOutput == null)
				return;

			if (ConsoleOutput.text.Length > 0)
				ConsoleOutput.text += "\n" + text;
			else
				ConsoleOutput.text += text;

			ConsoleScrollbar.value = 0;
		}

		public static void Clear()
		{
			if (Singleton.ConsoleOutput == null)
				return;

			Singleton.ConsoleOutput.text = "";
			Singleton.ConsoleScrollbar.value = 0;
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

			ConsoleScrollbar.value = 0;

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
					if (ConsoleOutput.text.Length > 0)
						ConsoleOutput.text += "\n" + BackgroundThreadBuffer;
					else
						ConsoleOutput.text += BackgroundThreadBuffer;
					ConsoleScrollbar.value = 0;
					BackgroundThreadBuffer = null;
				}
			}

			if (Keyboard.current.backquoteKey.wasPressedThisFrame)
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

			if (Keyboard.current.escapeKey.wasPressedThisFrame)
			{
				Cons.Close();
				return;
			}

			if (Keyboard.current.tabKey.wasPressedThisFrame)
				OnTab();


		}
	}

}
