using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using UnityEngine;

using Dreamwing.Cons;
using Unity.Pipeline.Commands;

using DevTools;

namespace DevTools.Cli
{
	// The game-side commands for Unity's Pipeline server, reached from the shell with
	// `unity command dev <name>` or `tools/ucmd`. Discovered automatically by [CliCommand] — no
	// registration. This assembly shares Unity.Pipeline's define constraint, so it exists only in
	// the editor and development builds. See docs/AgentTooling.md.
	//
	// Keep this layer thin: game-flow verbs belong in the dev console (Cons.AddCommand), where a
	// human can use them too, and `dev console` exposes all of them.
	internal static class DevCliCommands
	{
		// The console panel marks warnings/errors with rich-text <color> tags; strip them so
		// callers get plain text.
		private static readonly Regex RichTextTag =
			new Regex("</?color[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

		// The CommandContext form (Pipeline 1.0) rather than the legacy return-a-value one: a
		// failed console command must exit non-zero AND still hand back what it logged, and a
		// legacy command can only fail by throwing, which loses the output.
		[CliGroupCommand("dev", "run a dev-console command in the running game and return what it logged. Exits non-zero (still returning the output) when the line didn't run or the command logged an error. Commands that answer on a later frame through Cons.Defer (net server queries, net sync) are waited for, up to timeout seconds; other async results land in the Unity console instead — read them with the console command.",
			Name = "console", Tags = new[] { "devtools" }, Effect = CommandEffect.Destructive)]
		private static async Task<ExitCode> RunConsole(CommandContext context,
			[CliArg("cmd", "Console command line, e.g. \"net channels\" or \"help\"", Required = true)] string cmd,
			[CliArg("timeout", "Seconds to wait for a command that answers on a later frame (Cons.Defer)")] float timeout = 20f)
		{
			RequirePlaying();
			var (raw, failed) = await Cons.RunCommandCapturingOutputAsync(cmd, timeout);
			string output = RichTextTag.Replace(raw, "");
			context.Result.Write(new { output, failed });
			if (!failed)
				return ExitCode.Success;
			// The output again as the error: a player build's runtime server answers a failed
			// command with the logged error only, not its result
			context.Log.Error(output.Length > 0 ? output : "console command failed: " + cmd);
			return ExitCode.Failed;
		}

		[CliCommand("dev", "status", "Running-game state — frame, time scale, screen size (px), whether the game reports itself in-game (DevStatus.InGame; poll it to wait for a game to be ready), and whatever else the game reports (DevStatus.Details, under game).",
			Tags = new[] { "devtools" }, Effect = CommandEffect.ReadOnly)]
		private static object Status()
		{
			RequirePlaying();
			return new
			{
				frame = Time.frameCount,
				timeScale = Time.timeScale,
				screen = new { w = UiProbe.ScreenSize.x, h = UiProbe.ScreenSize.y },
				scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
				inGame = DevStatus.IsInGame(),
				net = NetInspector.Unavailable() ?? "connected",
				game = DevStatus.GetDetails(),
			};
		}

		// The structured form of the `ui dump` / `ui text` console verbs (UiProbe). Pressing things
		// stays in the console (`ui click`, `ui settext`, …), where a human can use it too.
		[CliCommand("dev", "ui_dump", "every active interactable UI element (path, type, label/text, toggle/slider value, interactable, selected, visible, screen rect in px from bottom-left, overlay = the dev console, what a pointer press at its centre would hit instead), optionally the visible texts too. Drive them with the `ui click/settext/toggle/clickat` console verbs.",
			Tags = new[] { "devtools" }, Effect = CommandEffect.ReadOnly)]
		private static object UiDump(
			[CliArg("filter", "Substring of path or label to keep")] string filter = null,
			[CliArg("texts", "Also list every visible Text")] bool texts = false,
			[CliArg("overlay", "Include the dev console / debug overlay")] bool overlay = false)
		{
			RequirePlaying();

			bool Keep(string path, string label) => string.IsNullOrEmpty(filter)
				|| path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
				|| (label != null && label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);

			var elements = new List<object>();
			foreach (UiElementInfo e in UiProbe.Collect(overlay))
			{
				if (!Keep(e.Path, e.Label))
					continue;
				elements.Add(new
				{
					path = e.Path,
					type = e.Type,
					label = e.Label,
					isOn = e.IsOn,
					value = e.Value,
					interactable = e.Interactable,
					selected = e.Selected,
					visible = e.Visible,
					rect = RectJson(e.ScreenRect),
					overlay = e.Overlay,
					hitTested = e.HitTested,
					hitAtCentre = e.HitAtCentre,
				});
			}

			List<object> textList = null;
			if (texts)
			{
				textList = new List<object>();
				foreach (var (path, text, rect) in UiProbe.CollectTexts(overlay))
					if (Keep(path, text))
						textList.Add(new { path, text, rect = RectJson(rect) });
			}

			return new
			{
				screen = new { w = UiProbe.ScreenSize.x, h = UiProbe.ScreenSize.y },
				elements,
				texts = textList,
			};
		}

		private static object RectJson(Rect r)
		{
			return new
			{
				x = Mathf.RoundToInt(r.x),
				y = Mathf.RoundToInt(r.y),
				w = Mathf.RoundToInt(r.width),
				h = Mathf.RoundToInt(r.height),
				cx = Mathf.RoundToInt(r.center.x),
				cy = Mathf.RoundToInt(r.center.y),
			};
		}

		// The editor's `capture game_view` doesn't exist in player builds, so this is how a dev build
		// gets screenshotted (`tools/ucmd --runtime <app> shot`). The capture happens at the end of the frame, after this
		// returns: poll for the file.
		[CliCommand("dev", "shot", "screenshot the running game (composited screen, incl. UI) to a PNG. Written at the end of the current frame — wait for the file to appear.",
			Tags = new[] { "devtools" }, Effect = CommandEffect.Idempotent)]
		private static object Shot(
			[CliArg("path", "Absolute path of the PNG to write", Required = true)] string path)
		{
			RequirePlaying();
			if (!System.IO.Path.IsPathRooted(path))
				throw new ArgumentException("path must be absolute");

			System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
			if (System.IO.File.Exists(path))
				System.IO.File.Delete(path);
			ScreenCapture.CaptureScreenshot(path);
			return new { path };
		}

		private static void RequirePlaying()
		{
			if (!Application.isPlaying)
				throw new InvalidOperationException("the game is not running (enter play mode first: tools/ucmd play)");
		}
	}
}
