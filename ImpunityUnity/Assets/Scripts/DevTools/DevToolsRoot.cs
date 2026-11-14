using System;

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

using Dreamwing.Cons;

namespace DevTools
{
	// Drop the DevTools prefab (this component) into a scene to get the agent/dev tooling (docs/AgentTooling.md):
	//   * the Dreamwing dev console (backquote toggles it; editor and development builds only),
	//     with the generic command groups — help/console/system, `net` (replicated-state
	//     inspection) and `ui` (driving uGUI without a mouse);
	//   * an EventSystem, when the scene has none;
	//   * what the `dev` Pipeline commands (DevTools.Cli) drive: `tools/ucmd game <line>` runs a
	//     console line, `dev status` reports DevStatus.
	//
	// The game plugs itself in through static hooks, set from its own startup code:
	//   DevStatus.InGame / DevStatus.Details  — what `dev status` (and `ucmd wait ingame`) report
	//   NetInspector.ConnectionSource, Me, …   — what the `net` verbs inspect
	//   Cons.AddCommandGroup / Cons.AddCommand — the game's own console verbs
	// Survives scene loads (DontDestroyOnLoad); a second copy destroys itself.
	[DefaultExecutionOrder(-1000)]
	public class DevToolsRoot : MonoBehaviour
	{
		public static DevToolsRoot Singleton { get; private set; }

		// The console panel (Assets/Prefabs/ConsolePrefab.prefab)
		public GameObject ConsolePrefab;

		// Height in screen pixels that maps to one console scale step; the console's 10 pt
		// text is drawn at floor(screen height / this) times its size
		public float PixelsPerScaleStep = 540.0f;

		// Raised when the console opens/closes — e.g. to
		// suspend the game's input while typing
		public static event Action ConsoleOpened;
		public static event Action ConsoleClosed;

		public Canvas OverlayCanvas { get; private set; }
		public Cons Console { get; private set; }

		private CanvasScaler OverlayScaler;
		private int LastScreenHeight;

		void Awake()
		{
			if (Singleton != null && Singleton != this)
			{
				Destroy(gameObject);
				return;
			}
			Singleton = this;
			DontDestroyOnLoad(gameObject);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
			// Editor play mode and dev builds keep running unfocused, so tools (and a second
			// window, in multiplayer tests) can drive them
			Application.runInBackground = true;
#endif

			Cons.Init();
			Cons.OnOpened = () => ConsoleOpened?.Invoke();
			Cons.OnClosed = () => ConsoleClosed?.Invoke();
			NetCommands.Register();
			UiCommands.Register();

			EnsureEventSystem();
			CreateOverlay();
		}

		void OnDestroy()
		{
			if (Singleton == this)
				Singleton = null;
		}

		void Update()
		{
			if (Screen.height != LastScreenHeight)
				UpdateScale();
		}

		private void CreateOverlay()
		{
#if UNITY_EDITOR || DEVELOPMENT_BUILD
			var overlay = new GameObject("DevOverlay", typeof(RectTransform));
			overlay.transform.SetParent(transform, false);
			overlay.layer = LayerMask.NameToLayer("UI");

			OverlayCanvas = overlay.AddComponent<Canvas>();
			OverlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
			OverlayCanvas.sortingOrder = 32000;   // above any game UI
			OverlayScaler = overlay.AddComponent<CanvasScaler>();
			OverlayScaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
			overlay.AddComponent<GraphicRaycaster>();
			UpdateScale();

			if (ConsolePrefab == null)
			{
				Debug.LogWarning("DevTools: no ConsolePrefab assigned, so there's no console panel (console commands still run from tools/ucmd)");
				return;
			}
			GameObject consObject = Instantiate(ConsolePrefab, overlay.transform);
			Console = consObject.GetComponent<Cons>();
#endif
		}

		private void UpdateScale()
		{
			LastScreenHeight = Screen.height;
			if (OverlayScaler != null)
				OverlayScaler.scaleFactor = Mathf.Max(1.0f, Mathf.Floor(Screen.height / PixelsPerScaleStep));
		}

		private static void EnsureEventSystem()
		{
			if (EventSystem.current != null || FindAnyObjectByType<EventSystem>() != null)
				return;
			var go = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
			DontDestroyOnLoad(go);
		}
	}
}
