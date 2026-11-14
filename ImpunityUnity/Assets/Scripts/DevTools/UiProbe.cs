using System;
using System.Collections.Generic;
using System.Text;

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

using Dreamwing.UI;

namespace DevTools
{
	// One interactable UI element as the probe sees it. Rects are screen pixels (Unity's screen
	// space: origin bottom-left, the coordinates `ui clickat` takes).
	public class UiElementInfo
	{
		public Selectable Selectable;
		public string Path;          // from the root canvas, `[n]` disambiguating same-named siblings
		public string Type;
		public string Label;         // the visible text: a button's caption, an input field's text
		public bool? IsOn;           // toggles
		public float? Value;         // sliders, scrollbars
		public bool Interactable;    // Selectable.IsInteractable(): includes CanvasGroups
		public bool Selected;        // the event system's current selection
		public bool Visible;         // on screen, non-zero size, not faded out by a CanvasGroup
		public Rect ScreenRect;
		public bool Overlay;         // part of the dev tools overlay (the console)
		public bool HitTested;       // a pointer raycast can reach this canvas (it has an active raycaster)
		public string HitAtCentre;   // what a pointer press at the centre would land on, when it isn't this
	}

	// Inspects and drives uGUI for the `ui` console verbs and `dev ui_dump`: what is on screen and
	// interactable, and pressing it the way a pointer would (ExecuteEvents, so `interactable`,
	// CanvasGroups and the controls' own handlers all apply — never `onClick.Invoke()`).
	//
	// Works with plain uGUI: Screen Space - Overlay canvases, and Screen Space - Camera / World Space
	// canvases whose camera draws to the screen (not to a render texture).
	public static class UiProbe
	{
		private static readonly List<RaycastResult> RaycastResults = new List<RaycastResult>();
		private static readonly Vector3[] Corners = new Vector3[4];

		// The game's screen in pixels. Not Screen.width/height, which outside the game view's own
		// frame (a Pipeline command, an eval) report a different size in the editor — e.g.
		// 3165x1865 for a 3161x1771 view. An overlay canvas's display size, or the main camera's
		// pixel size, is the size the game is actually drawn at.
		public static Vector2Int ScreenSize
		{
			get
			{
				Canvas overlay = DevToolsRoot.Singleton != null ? DevToolsRoot.Singleton.OverlayCanvas : null;
				if (overlay != null && overlay.renderingDisplaySize.x > 0)
					return Vector2Int.RoundToInt(overlay.renderingDisplaySize);
				Camera cam = Camera.main;
				if (cam != null && cam.targetTexture == null)
					return new Vector2Int(cam.pixelWidth, cam.pixelHeight);
				return new Vector2Int(Screen.width, Screen.height);
			}
		}

		// ---- Collection ----

		public static List<UiElementInfo> Collect(bool includeOverlay = false)
		{
			var list = new List<UiElementInfo>();
			foreach (Selectable selectable in Selectable.allSelectablesArray)
			{
				if (selectable == null || !selectable.isActiveAndEnabled)
					continue;

				Canvas root = RootCanvasOf(selectable);
				if (root == null)
					continue;
				if (!includeOverlay && IsDevOverlay(root))
					continue;

				list.Add(Describe(selectable, root));
			}

			list.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
			return list;
		}

		public static UiElementInfo Describe(Selectable selectable)
		{
			return Describe(selectable, RootCanvasOf(selectable));
		}

		private static UiElementInfo Describe(Selectable selectable, Canvas root)
		{
			var info = new UiElementInfo
			{
				Selectable = selectable,
				Path = PathOf(selectable.transform),
				Type = selectable.GetType().Name,
				Interactable = selectable.IsInteractable(),
				Selected = EventSystem.current != null && EventSystem.current.currentSelectedGameObject == selectable.gameObject,
				Overlay = IsDevOverlay(root),
			};

			DescribeValue(selectable, info);

			info.ScreenRect = TryGetScreenRect((RectTransform)selectable.transform, root, out Rect rect) ? rect : default;
			info.Visible = info.ScreenRect.width > 0.5f && info.ScreenRect.height > 0.5f
				&& info.ScreenRect.Overlaps(new Rect(Vector2.zero, ScreenSize))
				&& EffectiveAlpha(selectable.transform) > 0.01f;

			info.HitTested = HasActiveRaycaster(root);
			if (info.HitTested && info.Visible)
			{
				GameObject top = RaycastTop(info.ScreenRect.center);
				if (top == null)
					info.HitAtCentre = "nothing";
				else if (!top.transform.IsChildOf(selectable.transform))
					info.HitAtCentre = PathOf(top.transform);
			}

			return info;
		}

		private static void DescribeValue(Selectable selectable, UiElementInfo info)
		{
			switch (selectable)
			{
				case CustomInputField custom:
					info.Label = custom.text;
					break;
				case InputField field:
					info.Label = field.text;
					break;
				case Toggle toggle:
					info.IsOn = toggle.isOn;
					info.Label = FirstText(selectable.transform);
					break;
				case Slider slider:
					info.Value = slider.value;
					break;
				case Scrollbar scrollbar:
					info.Value = scrollbar.value;
					break;
				case Dropdown dropdown:
					info.Label = dropdown.captionText != null ? dropdown.captionText.text : null;
					break;
				default:
					info.Label = FirstText(selectable.transform);
					break;
			}
		}

		private static string FirstText(Transform t)
		{
			Text text = t.GetComponentInChildren<Text>();
			return text != null && text.text.Length > 0 ? text.text : null;
		}

		// Every visible Text, for "what does the screen say" (popups, prompts, labels next to controls).
		public static List<(string path, string text, Rect rect)> CollectTexts(bool includeOverlay = false)
		{
			var list = new List<(string, string, Rect)>();
			foreach (Text text in UnityEngine.Object.FindObjectsByType<Text>(FindObjectsSortMode.None))
			{
				if (!text.isActiveAndEnabled || string.IsNullOrWhiteSpace(text.text))
					continue;
				Canvas root = text.canvas != null ? text.canvas.rootCanvas : null;
				if (root == null || (!includeOverlay && IsDevOverlay(root)))
					continue;
				if (EffectiveAlpha(text.transform) * text.color.a <= 0.01f)
					continue;
				if (!TryGetScreenRect(text.rectTransform, root, out Rect rect))
					continue;
				list.Add((PathOf(text.transform), text.text, rect));
			}
			list.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
			return list;
		}

		// ---- Resolving a target ----

		// A target is a path (exact), a path suffix on a '/' boundary, or a label (case-insensitive).
		// Exactly one element must match, or the error lists the candidates.
		public static bool TryResolve(string spec, out UiElementInfo element, out string error)
		{
			element = null;
			error = null;
			if (string.IsNullOrWhiteSpace(spec))
			{
				error = "no target given";
				return false;
			}

			List<UiElementInfo> all = Collect(includeOverlay: true);

			List<UiElementInfo> matches = all.FindAll(e => e.Path == spec);
			if (matches.Count == 0)
				matches = all.FindAll(e => e.Path.EndsWith("/" + spec, StringComparison.Ordinal));
			if (matches.Count == 0)
				matches = all.FindAll(e => e.Label != null && string.Equals(e.Label.Trim(), spec.Trim(), StringComparison.OrdinalIgnoreCase));

			if (matches.Count == 1)
			{
				element = matches[0];
				return true;
			}

			if (matches.Count == 0)
			{
				error = "nothing matches '" + spec + "' (a path, path suffix or label — see `ui dump`)";
				return false;
			}

			var sb = new StringBuilder("'" + spec + "' is ambiguous, " + matches.Count + " match:");
			foreach (UiElementInfo m in matches)
				sb.Append("\n  ").Append(m.Path);
			error = sb.ToString();
			return false;
		}

		// ---- Triggers ----

		// A pointer click on the element: enter, down, up, click (or submit, for a control that
		// only handles submit), then exit. Refuses what a real press couldn't trigger.
		public static bool Click(UiElementInfo element, out string error)
		{
			Selectable selectable = element.Selectable;
			if (selectable == null || !selectable.isActiveAndEnabled)
			{
				error = "'" + element.Path + "' is no longer active";
				return false;
			}
			if (!selectable.IsInteractable())
			{
				error = "'" + element.Path + "' is not interactable";
				return false;
			}
			if (EventSystem.current == null)
			{
				error = "there is no EventSystem";
				return false;
			}

			GameObject go = selectable.gameObject;
			PointerEventData data = NewPointerEvent(element.ScreenRect.center);
			data.pointerEnter = go;

			ExecuteEvents.Execute(go, data, ExecuteEvents.pointerEnterHandler);
			Press(go, go, data);
			ExecuteEvents.Execute(go, data, ExecuteEvents.pointerExitHandler);

			error = null;
			return true;
		}

		// A pointer press at a screen position, exactly where the input module would deliver it:
		// the top raycast hit wins.
		public static bool ClickAt(Vector2 screenPos, out string hitPath, out string error)
		{
			hitPath = null;
			if (EventSystem.current == null)
			{
				error = "there is no EventSystem";
				return false;
			}
			GameObject top = RaycastTop(screenPos);
			if (top == null)
			{
				error = "nothing raycastable at (" + screenPos.x + "," + screenPos.y + ")";
				return false;
			}
			hitPath = PathOf(top.transform);

			PointerEventData data = NewPointerEvent(screenPos);
			data.pointerCurrentRaycast = data.pointerPressRaycast = new RaycastResult { gameObject = top, screenPosition = screenPos };
			GameObject entered = ExecuteEvents.GetEventHandler<IPointerEnterHandler>(top);
			if (entered != null)
			{
				data.pointerEnter = entered;
				ExecuteEvents.Execute(entered, data, ExecuteEvents.pointerEnterHandler);
			}

			Press(top, top, data);

			if (entered != null)
				ExecuteEvents.Execute(entered, data, ExecuteEvents.pointerExitHandler);

			error = null;
			return true;
		}

		// down → up → click on the handlers above `hit`, as InputSystemUIInputModule does
		private static void Press(GameObject hit, GameObject selectTarget, PointerEventData data)
		{
			data.eligibleForClick = true;
			data.pressPosition = data.position;
			data.clickCount = 1;
			data.clickTime = Time.unscaledTime;

			GameObject pressed = ExecuteEvents.ExecuteHierarchy(hit, data, ExecuteEvents.pointerDownHandler);
			GameObject clickHandler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit);
			if (pressed == null)
				pressed = clickHandler;

			data.pointerPress = pressed;
			data.rawPointerPress = hit;

			if (pressed != null)
				ExecuteEvents.Execute(pressed, data, ExecuteEvents.pointerUpHandler);

			if (pressed != null && pressed == clickHandler)
				ExecuteEvents.Execute(pressed, data, ExecuteEvents.pointerClickHandler);
			else if (ExecuteEvents.GetEventHandler<ISubmitHandler>(selectTarget) is GameObject submit)
				ExecuteEvents.Execute(submit, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);

			data.pointerPress = null;
			data.rawPointerPress = null;
		}

		// Like typing it: the field's own SetText applies its validation and character limit and
		// fires onValueChanged (once, not per character), then editing ends (onEndEdit).
		public static bool SetText(UiElementInfo element, string value, out string result, out string error)
		{
			result = null;
			Selectable selectable = element.Selectable;
			if (selectable == null || !selectable.IsInteractable())
			{
				error = "'" + element.Path + "' is not interactable";
				return false;
			}

			switch (selectable)
			{
				case CustomInputField custom:
					if (custom.readOnly) { error = "'" + element.Path + "' is read-only"; return false; }
					custom.text = value;
					custom.onEndEdit.Invoke(custom.text);
					result = custom.text;
					break;
				case InputField field:
					if (field.readOnly) { error = "'" + element.Path + "' is read-only"; return false; }
					field.text = value;
					field.onEndEdit.Invoke(field.text);
					result = field.text;
					break;
				default:
					error = "'" + element.Path + "' is a " + element.Type + ", not a text field";
					return false;
			}

			error = null;
			return true;
		}

		// ---- Geometry ----

		// The element's rect in screen pixels
		public static bool TryGetScreenRect(RectTransform rt, Canvas root, out Rect rect)
		{
			rect = default;
			if (root == null)
				return false;

			rt.GetWorldCorners(Corners);
			float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
			for (int i = 0; i < 4; i++)
			{
				Vector2 p = WorldToScreen(Corners[i], root);
				minX = Mathf.Min(minX, p.x);
				minY = Mathf.Min(minY, p.y);
				maxX = Mathf.Max(maxX, p.x);
				maxY = Mathf.Max(maxY, p.y);
			}

			rect = Rect.MinMaxRect(minX, minY, maxX, maxY);
			return true;
		}

		private static Vector2 WorldToScreen(Vector3 world, Canvas root)
		{
			Camera cam = root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
			if (cam == null)
				return world; // overlay canvases are laid out in screen pixels
			// A camera drawing into a render texture gives texture pixels: right only when that
			// texture is shown 1:1 on screen
			return cam.WorldToScreenPoint(world);
		}

		// ---- Helpers ----

		public static GameObject RaycastTop(Vector2 screenPos)
		{
			EventSystem events = EventSystem.current;
			if (events == null)
				return null;
			var data = new PointerEventData(events) { position = screenPos };
			events.RaycastAll(data, RaycastResults);
			GameObject top = RaycastResults.Count > 0 ? RaycastResults[0].gameObject : null;
			RaycastResults.Clear();
			return top;
		}

		private static PointerEventData NewPointerEvent(Vector2 screenPos)
		{
			return new PointerEventData(EventSystem.current)
			{
				button = PointerEventData.InputButton.Left,
				position = screenPos,
				pointerId = PointerInputModule.kMouseLeftId,
			};
		}

		private static Canvas RootCanvasOf(Component c)
		{
			Canvas canvas = c.GetComponentInParent<Canvas>();
			return canvas != null ? canvas.rootCanvas : null;
		}

		// The dev console's overlay: left out of dumps unless asked for
		private static bool IsDevOverlay(Canvas root)
		{
			return DevToolsRoot.Singleton != null && root == DevToolsRoot.Singleton.OverlayCanvas;
		}

		private static bool HasActiveRaycaster(Canvas root)
		{
			BaseRaycaster raycaster = root.GetComponent<BaseRaycaster>();
			return raycaster != null && raycaster.isActiveAndEnabled;
		}

		private static float EffectiveAlpha(Transform t)
		{
			float alpha = 1f;
			for (; t != null; t = t.parent)
			{
				CanvasGroup group = t.GetComponent<CanvasGroup>();
				if (group == null || !group.enabled)
					continue;
				alpha *= group.alpha;
				if (group.ignoreParentGroups)
					break;
			}
			return alpha;
		}

		// From the root canvas down; `name[n]` (n = 0-based among same-named siblings) only where
		// a name repeats, so paths stay readable and stable as long as the hierarchy does.
		public static string PathOf(Transform t)
		{
			var parts = new List<string>();
			for (; t != null; t = t.parent)
			{
				parts.Add(SegmentOf(t));
				Canvas canvas = t.GetComponent<Canvas>();
				if (canvas != null && canvas.isRootCanvas)
					break;
			}
			parts.Reverse();
			return string.Join("/", parts);
		}

		private static string SegmentOf(Transform t)
		{
			Transform parent = t.parent;
			if (parent == null)
				return t.name;

			int index = 0, count = 0;
			for (int i = 0; i < parent.childCount; i++)
			{
				Transform sibling = parent.GetChild(i);
				if (sibling.name != t.name)
					continue;
				if (sibling == t)
					index = count;
				count++;
			}
			return count > 1 ? t.name + "[" + index + "]" : t.name;
		}
	}
}
