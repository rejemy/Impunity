using System;
using System.Collections.Generic;
using System.Text;

using UnityEngine;
using UnityEngine.UI;

using Dreamwing.Cons;


namespace DevTools
{
	// `ui ...`: inspecting and pressing any uGUI without a mouse (dump, text, click, settext, toggle,
	// clickat — see UiProbe), plus fullscreen. A game adds its own verbs to the same group.
	public static class UiCommands
	{
		public static void Register()
		{
			Cons.AddCommandGroup("ui", "Inspecting and pressing UI without a mouse; the window");

			Cons.AddCommand("ui fullscreen", ToggleFullscreen, "Toggles between fullscreen and windowed");

			// Inspecting and pressing UI without a mouse (UiProbe). A target is a path from `ui dump`,
			// a unique path suffix, or a unique label.
			Cons.AddCommand<string>("ui dump", Dump,
				"Lists the active interactable UI (path, label, screen rect, state); filter = substring of path or label, 'all' adds the dev console",
				new CommandStringArgument("filter", ""));

			Cons.AddCommand<string>("ui text", DumpTexts,
				"Lists the visible text on screen (popups, prompts, labels); filter = substring of path or text",
				new CommandStringArgument("filter", ""));

			Cons.AddCommand<string>("ui click", Click,
				"Clicks a UI element as a pointer would (pointer down/up/click events; refuses non-interactable). target = path, path suffix or label",
				new CommandStringArgument("target"));

			Cons.AddCommand<string, string>("ui settext", SetText,
				"Sets a text field as typing would (its validation, onValueChanged, then onEndEdit)",
				new CommandStringArgument("target"),
				new CommandStringArgument("value"));

			Cons.AddCommand<string, string>("ui toggle", SetToggle,
				"Clicks a toggle to the given state: on, off or flip",
				new CommandStringArgument("target"),
				new CommandStringArgument("state", "flip"));

			Cons.AddCommand<int, int>("ui clickat", ClickAt,
				"Presses whatever UI the pointer would hit at screen pixel x,y (origin bottom-left, as `ui dump` reports) — catches blockers and wrong hit rects",
				new CommandIntArgument("x"),
				new CommandIntArgument("y"));
		}

		private static void Dump(string filter)
		{
			bool all = filter == "all";
			if (all)
				filter = "";

			List<UiElementInfo> elements = UiProbe.Collect(includeOverlay: all);
			int shown = 0;
			foreach (UiElementInfo e in elements)
			{
				if (!Matches(filter, e.Path, e.Label))
					continue;
				shown++;
				Cons.Log(FormatElement(e));
			}

			Cons.Log("ui dump: " + shown + " of " + elements.Count + " element(s); screen " + UiProbe.ScreenSize.x + "x" + UiProbe.ScreenSize.y
				+ ", rects in screen px from bottom-left");
		}

		private static string FormatElement(UiElementInfo e)
		{
			var sb = new StringBuilder();
			if (e.Overlay)
				sb.Append("[dev] ");
			sb.Append(e.Type);
			if (e.Label != null)
				sb.Append(" \"").Append(e.Label.Replace("\n", "\\n")).Append('"');
			if (e.IsOn.HasValue)
				sb.Append(e.IsOn.Value ? " [on]" : " [off]");
			if (e.Value.HasValue)
				sb.Append(" =").Append(e.Value.Value.ToString("0.###"));
			sb.Append("  ").Append(e.Path);
			Rect r = e.ScreenRect;
			sb.Append("  @").Append(Mathf.RoundToInt(r.center.x)).Append(',').Append(Mathf.RoundToInt(r.center.y))
				.Append(' ').Append(Mathf.RoundToInt(r.width)).Append('x').Append(Mathf.RoundToInt(r.height));

			if (!e.Interactable)
				sb.Append("  disabled");
			if (e.Selected)
				sb.Append("  selected");
			if (!e.Visible)
				sb.Append("  hidden");
			if (e.HitAtCentre != null)
				sb.Append("  covered-by=").Append(e.HitAtCentre);
			return sb.ToString();
		}

		private static void DumpTexts(string filter)
		{
			var texts = UiProbe.CollectTexts(includeOverlay: filter == "all");
			if (filter == "all")
				filter = "";
			int shown = 0;
			foreach (var (path, text, rect) in texts)
			{
				if (!Matches(filter, path, text))
					continue;
				shown++;
				Cons.Log("\"" + text.Replace("\n", "\\n") + "\"  " + path + "  @" + Mathf.RoundToInt(rect.center.x) + "," + Mathf.RoundToInt(rect.center.y));
			}
			Cons.Log("ui text: " + shown + " of " + texts.Count + " text(s)");
		}

		private static bool Matches(string filter, string path, string label)
		{
			return string.IsNullOrEmpty(filter)
				|| path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
				|| (label != null && label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
		}

		private static bool Resolve(string cmd, string target, out UiElementInfo element)
		{
			if (UiProbe.TryResolve(target, out element, out string error))
				return true;
			Cons.LogError(cmd + ": " + error);
			return false;
		}

		private static void Click(string target)
		{
			if (!Resolve("ui click", target, out UiElementInfo e))
				return;
			if (!UiProbe.Click(e, out string error))
			{
				Cons.LogError("ui click: " + error);
				return;
			}
			Cons.Log("ui click: " + e.Path + Notes(e));
		}

		// What a real pointer or controller would have run into, reported but not refused: the
		// semantic click is also how gamepad-only UI gets pressed
		private static string Notes(UiElementInfo e)
		{
			string notes = "";
			if (e.HitAtCentre != null)
				notes += " (note: a pointer at its centre would hit " + e.HitAtCentre + " instead)";
			if (!e.Visible)
				notes += " (note: not visible)";
			return notes;
		}

		private static void SetText(string target, string value)
		{
			if (!Resolve("ui settext", target, out UiElementInfo e))
				return;
			if (!UiProbe.SetText(e, value, out string result, out string error))
			{
				Cons.LogError("ui settext: " + error);
				return;
			}
			Cons.Log("ui settext: " + e.Path + " = \"" + result + "\"" + (result != value ? " (the field changed it)" : "") + Notes(e));
		}

		private static void SetToggle(string target, string state)
		{
			if (!Resolve("ui toggle", target, out UiElementInfo e))
				return;
			if (!(e.Selectable is Toggle toggle))
			{
				Cons.LogError("ui toggle: '" + e.Path + "' is a " + e.Type + ", not a toggle");
				return;
			}

			bool want;
			switch (state.ToLowerInvariant())
			{
				case "on": case "true": case "1": want = true; break;
				case "off": case "false": case "0": want = false; break;
				case "flip": want = !toggle.isOn; break;
				default:
					Cons.LogError("ui toggle: state must be on, off or flip, not '" + state + "'");
					return;
			}

			if (toggle.isOn != want && !UiProbe.Click(e, out string error))
			{
				Cons.LogError("ui toggle: " + error);
				return;
			}
			if (toggle.isOn != want)
			{
				Cons.LogError("ui toggle: clicked, but it stayed " + (toggle.isOn ? "on" : "off") + " (a toggle group that disallows switching off?)");
				return;
			}
			Cons.Log("ui toggle: " + e.Path + " is " + (toggle.isOn ? "on" : "off") + Notes(e));
		}

		private static void ClickAt(int x, int y)
		{
			if (!UiProbe.ClickAt(new Vector2(x, y), out string hit, out string error))
			{
				Cons.LogError("ui clickat: " + error);
				return;
			}
			Cons.Log("ui clickat: pressed " + hit);
		}

		private static void ToggleFullscreen()
		{
			Screen.fullScreen = !Screen.fullScreen;
			Cons.Log("ui fullscreen: " + (Screen.fullScreen ? "on" : "off"));
		}
	}
}
