# Agent Tooling — driving the editor and the game from the shell

How an agent (or a script) compiles, tests, plays, inspects, and screenshots the ImpunityUnity
project without a human at the editor. Ported on 2026-11-14 from the Brambleville game project,
where it was built and battle-tested; everything here is the game-independent part. Built on
Unity's **official** tooling:

- **Unity CLI** (`unity`, **1.0.0**) — `~/.unity/bin/unity`. `unity command <group> <name>` calls a
  command on a running editor. Docs: <https://docs.unity.com/en-us/unity-cli/unity-cli-reference>
- **Pipeline package** (`com.unity.pipeline`, **1.0.0**, pinned in `Packages/manifest.json`) — an
  HTTP server inside the editor (and optionally dev player builds) exposing ~170 commands, plus our
  own `[CliCommand]`s. Package docs: `Library/PackageCache/com.unity.pipeline@*/Documentation~/`
  (`command-reference.md` lists every command).
- **`tools/ucmd`** — our thin wrapper over the two. **Use it by default.**

All paths and commands below are relative to `ImpunityUnity/` (the Unity project root): run
`tools/ucmd …` from there.

**Commands are grouped**: `editor status`, `script recompile`, `gameobject find`,
`editor.playmode start`, … `unity command` alone lists the groups, `unity command <group>` one
group's commands. **Parameters are kebab-case** (`--async-tests`, `--since-session`, `--dry-run`);
`unity command <group> <name> --help` prints a command's parameters with types and defaults.

**Output contract:** `--format json` prints the command's **bare result** on stdout and exits 0.
List commands stream **one JSON record per line**. `script eval` returns the eval's value itself
(`return "pong";` → `pong`). A failure is a non-zero exit with the error on stderr; some failures
still write their reply to stdout (`test status` exits 6 when a test failed or none matched, with
the full summary). A result reporting `success:false` (or status rejected/failed/busy) exits 6 as
well. Exit codes: 2 usage · 6 command failed · 12 no Pipeline instance found (mid-reload, or no
editor) · 13 no such command · 14 timed out · 17 `UNAVAILABLE` (loading/compiling/reloading/busy —
retry) · 18 `AWAITING_INPUT` (a modal dialog) · 130 cancelled.

---

## Quick start

```bash
tools/ucmd status                    # editor state (+ game state while playing)
tools/ucmd compile                   # make C# edits live; prints compiler errors, exit 2 on failure
tools/ucmd tests editmode [filter]   # run tests; summary + failures, exit 8 on failures
tools/ucmd play                      # enter play mode and wait
tools/ucmd game help                 # any dev-console command, in the running game
tools/ucmd wait ingame               # until the game reports itself ready (DevStatus.InGame)
tools/ucmd logs                      # warnings + errors; prints cursor=... last
tools/ucmd shot                      # composited game-view PNG → Temp/ucmd/, prints the path
tools/ucmd stop
```

The verify loop: `compile` → `tests` → `play` → drive with `game` → `logs --since <cursor>` →
`shot` → `stop`.

**Unity does not need to be the foreground app** for any of this: compile, domain reload, play-mode
entry/exit and tests all run with the editor unfocused (the Pipeline server keeps the editor
ticking). `editor.playmode start` turns Run In Background on for the session, and `DevToolsRoot`
sets `Application.runInBackground` in the editor and development builds. Foreground Unity only when
a human is watching, or for real device input.

**When the work is done**, run `tools/work-done` — it brings Claude.app to the foreground as a
"ready for input" cue.

---

## What's in the project

| Piece | Where | What |
|---|---|---|
| `tools/ucmd` | `tools/` | The shell wrapper (below). |
| `tools/work-done` | `tools/` | Foregrounds Claude.app at the end of a session. |
| Dev console | `Assets/Scripts/Dreamwing/Cons/` (asmdef `Dreamwing`) | The Dreamwing console: a tree of command groups, Tab completion, help, output capture for tools. See [DevConsole.md](DevConsole.md). |
| Console panel | `Assets/Prefabs/ConsolePrefab.prefab` | The on-screen panel; backquote toggles it. |
| DevTools | `Assets/Scripts/DevTools/` (asmdef `DevTools`) · `Assets/Prefabs/DevTools.prefab` | `DevToolsRoot` (bootstrap), `DevStatus` (game hooks for `dev status`), `NetInspector` + `NetCommands` (replicated-state inspection, `net …`), `UiProbe` + `UiCommands` (driving uGUI, `ui …`). |
| `dev` Pipeline commands | `Assets/Scripts/DevTools/Cli/` (asmdef `DevTools.Cli`) | `dev console`, `dev status`, `dev ui_dump`, `dev shot`, `dev net_*`; `RuntimePipelineStarter` for dev player builds. Compiled only into the editor and development builds that enable the Pipeline (the package's define constraints), so release builds carry none of it. |
| Tests | `Assets/Tests/DevTools/` (asmdef `DevTools.Tests`, Edit Mode) | Console dispatch/help/completion, column layout, deferred replies, `NetInspector.ToPlain`, `UiProbe.PathOf`. `tools/ucmd tests editmode DevTools.Tests`. |
| Scene | `Assets/Scenes/TestGameScene.unity` | Has the `DevTools` prefab. |

### Setting up a scene

Drop `Assets/Prefabs/DevTools.prefab` into the scene. On `Awake` (execution order −1000) it:

- calls `Cons.Init()` (idempotent) and registers the generic groups (`help`, `quit`, `console`,
  `system`, `net`, `ui`);
- creates an EventSystem (`InputSystemUIInputModule`) if the scene has none;
- in the editor and development builds, creates a Screen Space - Overlay canvas (`DevOverlay`, sort
  order 32000, scaled by `floor(screen height / PixelsPerScaleStep)`) and instantiates the console
  panel in it;
- marks itself `DontDestroyOnLoad`; a second copy destroys itself.

Then the game plugs itself in from its own startup code:

```csharp
DevStatus.InGame = () => connection != null && connection.Connected;   // what `wait ingame` waits for
DevStatus.Details = () => new { mode = "local", players = 1 };          // anything else, under `game`
NetInspector.ConnectionSource = () => connection;                       // what `net …` inspects (required)
NetInspector.Me = () => myPlayerEntity;                                 // the reference `me`
NetInspector.Labeler = e => e is MyPlayer p ? "player " + p.Name.Get() : null;
NetInspector.LocalProjection = (e, field) => e is MyCharacter && field == "Position";
DevToolsRoot.ConsoleOpened += () => myInput.Suspend();                  // optional
Cons.AddCommandGroup("demo", "The demo's own verbs");                   // the game's commands
Cons.AddCommand("demo connect", Connect, "Connects to the standalone server");
```

---

## `tools/ucmd`

A Python script (`tools/ucmd --help` for the full usage). What it adds over raw `unity command`:

- **Always targets this project** (`--project-path`), so a second open editor (another Unity
  project) can't steal calls.
- **Rides out domain reloads.** The server is unreachable for ~1.5–2.5s during every reload
  (compile, entering/leaving play mode). Connection failures are retried for `UCMD_RETRY_SEC`
  (default 30): exits 12 and 17 and "unreachable" messages. A connection *reset mid-request*
  ("socket hang up", exit 6) is retried only for commands that are harmless to repeat — never
  `script eval`, `test run`, or a mutation.
- **Can target a player build** — `tools/ucmd --runtime <Game.app> <verb>` (or `UCMD_RUNTIME`) drives
  a running development build instead of the editor (see [Player builds](#player-builds)).
- **Prints just the result**; exits non-zero if the CLI fails or the result itself reports failure.
  A failed `script run` / `script eval` prints its error and stack trace (file:line), and compile
  errors one per line with line and column.

### Verbs

| Verb | Does |
|---|---|
| `status` | `editor status`, plus `dev status` under `game` while playing. |
| `compile` | `script recompile` → poll `script recompile_status` → wait for the domain reload to settle. Exit 2 with the compiler errors on failure; `up to date` when nothing changed. |
| `play [--game [timeout]]` | Enter play mode and wait until settled. Refuses (fast) when scripts have compile errors. `--game` also waits for `dev status` `inGame`. |
| `stop` | Exit play mode and wait. |
| `tests [editmode\|playmode] [filter]` | Async `test run` + poll `test status --detail failures` (file-backed, survives the play-mode reload). `filter` is a case-insensitive partial test-name match. Exit 8 on failures **or when no test matched**. |
| `game <console command…>` | `dev console` — run a dev-console command, print what it logged. **Exit 1 when the line failed**: unknown command, bad arguments, a throw, or an error logged while it ran. Works the same against a player build. Quoting: one argument is the whole line as typed (`game "net entity player1"`); with several, an argument the shell kept whole despite spaces is re-quoted for the console (`game ui click "Connect to server"`). |
| `logs [--all] [--since CURSOR] [--tail N]` | Unity console buffer (editor **and** game logs; default warnings+errors, newest 50). Last line is `cursor=<session>:<seq>`; pass it to `--since` to see only newer entries. The buffer survives domain reloads. |
| `shot [path]` | `capture game_view` — in play mode `source=screen` (composited backbuffer incl. overlay UI), else the camera. Written to `Temp/ucmd/shot-<ms>.png` unless a path is given. In play mode it keeps the game's aspect (longest edge 1920, size from `dev status` `screen`), so shot px × `screen.w / shot width` = screen px, y flipped — see [Driving UI](#driving-ui). |
| `tree [scenePath] [--depth N]` | Compact indented GameObject tree (child counts, `(inactive)`, component names) from `scene get_hierarchy`, all open scenes by default. |
| `wait <playing\|stopped\|ready\|ingame> [timeout]` | Block until the condition holds (default 120s). `ingame` = `dev status` `inGame` (`DevStatus.InGame`). |
| `list [query]` | List available commands, optionally filtered. |

### Raw commands

Anything else: `tools/ucmd <group> <command> --param value …` (a `global` command is one word).
Discover with `tools/ucmd list <query>`, `unity command <group> <name> --help`, or
`unity command --format json --detail full --limit 500` (full parameter schemas plus each
command's `readOnly`/`destructive`/`idempotent` effect). The ones we reach for:

| Need | Command |
|---|---|
| Find objects | `editor search --query "h:<name part>"` · `gameobject find --name X` / `--hierarchy-path /A/B` / `--type Camera` |
| Inspect a component | `component get_properties`, `component get_fields` |
| Run C# in the live editor/game | `script eval --code '…'` · `script eval --file x.cs` · `script run --file x.cs` (see [Authoring](#authoring-ui-and-prefabs)). `--auto-import true` adds a missing `using`; `--internals true` reaches Unity internals. |
| Build or edit prefabs / UI | `script run` builder, see [Authoring UI and prefabs](#authoring-ui-and-prefabs) |
| Menu items | `editor invoke_menu --path "…"` (omit `--path` to list) |
| Scenes | `scene open --path Scenes/TestGameScene`, `scene list_open` |
| Pause | `editor.playmode pause` (toggles) |
| Wait on any value | `job wait --condition '{"member":"…","op":"equals","value":…}'` |
| Perf | `diagnostics get_performance` |
| Builds | `build …` group (`tools/ucmd list build`) |
| Point the human at something | `gameobject set_selection` |

---

## The `dev` commands

Game-side commands for the Pipeline server, in `Assets/Scripts/DevTools/Cli/`. `unity command dev`
lists them. All fail with "the game is not running" outside play mode.

| Command | Does |
|---|---|
| `dev console --cmd "<line>"` | Run a dev-console command in the running game; returns `{output, failed}` (rich-text tags stripped). A failed line **exits 6**, with the result still on stdout and the output repeated as the error on stderr (a player build's runtime server returns only that error, as an HTTP 400). Commands that answer on a later frame through `Cons.Defer` (`net server …`, `net sync`) are **waited for**, up to `--timeout` seconds (default 20). Other output that arrives on a later frame goes to the Unity console — read it with `logs`. |
| `dev status` | `{frame, timeScale, screen:{w,h}, scene, inGame, net, game}` — `inGame` from `DevStatus.InGame`, `net` = `"connected"` or why `net …` can't inspect yet, `game` = `DevStatus.Details`. `screen` is the game's real pixel size (see the `Screen.width` trap). |
| `dev ui_dump [--filter s] [--texts true] [--overlay true]` | Every active interactable UI element, structured: `{screen, elements:[{path, type, label, isOn, value, interactable, selected, visible, rect:{x,y,w,h,cx,cy}, overlay, hitTested, hitAtCentre}], texts}`. The JSON form of `ui dump` / `ui text`. |
| `dev shot --path <abs.png>` | Screenshot via `ScreenCapture` (written at the end of the frame). For player builds, which lack `capture game_view`; `ucmd --runtime … shot` uses it. |
| `dev net_channels [--server true] [--prefix p]` · `dev net_channel --name <c> [--server true]` · `dev net_entity --entity <ref> [--server true]` · `dev net_sync [--target all\|<channel>\|<ref>] [--full true]` · `dev net_conns` | Replicated state as JSON — see [Inspecting replicated state](#inspecting-replicated-state). `net_sync` **exits 6 when anything is out of sync**, the report still on stdout. |

**Game-flow verbs belong in the dev console** (`Cons.AddCommand`), so a human can use them in-game
too — reach them with `tools/ucmd game …`. `tools/ucmd game help --all` lists every command with
its arguments; `help --find <text>` searches them. Add a `dev` command only when the caller needs
**structured** results.

### Adding a command

A static method anywhere in the `DevTools.Cli` assembly (or another assembly with the same define
constraints and a reference to `Unity.Pipeline`); it's discovered after the next compile:

```csharp
[CliCommand("dev", "thing", "What it does (shown in `list`)", Tags = new[] { "devtools" },
	Effect = CommandEffect.ReadOnly)]
private static object Thing([CliArg("name", "What to look up", Required = true)] string name)
{
	return new { found = true };   // anonymous object → result; throw for failure
}
```

- Runs on the main thread by default (`MainThreadRequired = true`), so game state is safe to touch.
- Return any serializable value; **throw** to fail (exit 6, the message on stderr).
- **Declare its `Effect`**: `ReadOnly`, `Idempotent`, `Additive`, `Destructive` (can lose work, or
  runs caller-supplied code, like `dev console`).
- This is the **legacy signature**, still supported. The preferred form is `[CliGroupCommand]`
  taking a `CommandContext` and returning `ExitCode` (or `Task<ExitCode>` for commands that wait
  across frames): results through `context.Result.Write`, logs through `context.Log`. Use it when a
  command must fail **and** return a result, stream records, or await something (`dev console` and
  the `dev net_*` commands do).
- The two-argument `[CliCommand(name, …)]` form puts an undotted name in the `global` group, where
  a clash with a package command is refused — keep ours in `dev`.
- Parameter order and required-ness are wire API for positional callers — append new optional
  params at the end.
- Full authoring guide: package `Documentation~/creating-commands.md`.

### Driving UI

Every uGUI element on screen can be listed and pressed from the shell, the way a pointer or a
typist would (`UiProbe`, the `ui` console group, `dev ui_dump`):

| Verb | Does |
|---|---|
| `ui dump [filter]` | One line per active `Selectable`: `[dev]` for the console's own, type, label (a button's caption, a field's text, `[on]`/`[off]`, `=value`), path, `@centreX,centreY WxH` in screen px, and flags — `disabled`, `selected`, `hidden`, `covered-by=<path>` (what a pointer press at its centre would hit instead). `filter` matches path or label; `all` adds the dev console. |
| `ui text [filter]` | Every visible `Text` with its path and centre: what the screen *says*. |
| `ui click <target>` | Pointer enter → down → up → click (or submit, for a submit-only control) → exit, through `ExecuteEvents`, so `interactable`, CanvasGroups and the control's own handlers apply. **Refuses a non-interactable target** (exit 1). Reaching what a real pointer couldn't (covered, hidden) is allowed, with a `(note: …)`. |
| `ui settext <target> <value>` | Sets a `CustomInputField` / `InputField` through its own `text` setter (validation, character limit, `onValueChanged` once), then fires `onEndEdit`. |
| `ui toggle <target> [on\|off\|flip]` | Clicks a `Toggle` until it has that state. |
| `ui clickat <x> <y>` | A press at a screen position, delivered where the input module would: `RaycastAll` on the current EventSystem, the top hit wins. Catches invisible blockers and wrong hit rects. |
| `ui fullscreen` | Toggles fullscreen. |

**Targets** (`click`, `settext`, `toggle`): an exact path, else a unique path suffix on a `/`
boundary (`OkButton`, `Dialog/OkButton`), else a unique label, case-insensitive. An ambiguous target
fails and lists the candidates. Paths run from the root canvas; a name repeated among siblings gets
`[n]` (0-based among the same-named).

**Coordinates** are screen pixels with the origin at the **bottom-left** (Unity's screen space), of
the size `ui dump`'s last line / `dev status` `screen` report. A shot from `ucmd shot` keeps the
aspect, so `screenX = shotX × screen.w / shotWidth` and
`screenY = screen.h − shotY × screen.w / shotWidth`.

Scope: legacy uGUI `Selectable`s and `Text` (TMP text isn't read), Screen Space - Overlay canvases
and camera canvases whose camera draws straight to the screen (a camera rendering into a render
texture gives texture coordinates). Sliders/scrollbars/dropdowns are listed but have no setter verb;
drags aren't simulated; there's no keyboard/gamepad navigation verb.

### Driving input

Device-level input can't be scripted reliably: macOS refuses `osascript` keystrokes without
Accessibility permission, and Pipeline's `runtime simulate_key --action down` queues a real Input
System event but in the editor the key reads released again by the next frame. Drive the game
**logically** instead: give it console verbs that call the same code a press would (Brambleville's
`input press <LogicalControl>` injected below the bindings into its mode handlers), and press UI
with the `ui` verbs.

### Inspecting replicated state

Every replicated entity can be read from the shell — this client's copy, the server's, and whether
they agree — so a check can say "the change is in replicated state", not just "the view moved", and
a desync shows up as one. Built on Impunity's inspection API (`DescribeChannels` /
`DescribeChannel` / `GetEntitySnapshot` / `DescribeConnections` on the connection,
`GetFieldsAsBson` / `DecodeSnapshot` / `CheckSync` on the entity manager; guide
`../docs/guides/DistributedEntities.md`, "Inspecting live state"), through `NetInspector`.
**Needs `NetInspector.ConnectionSource`** (see [Setting up a scene](#setting-up-a-scene)); until
then every verb says so. Console verbs (`NetCommands`), each with a `dev net_*` JSON twin
(`NetCliCommands`):

| Verb | Shows |
|---|---|
| `net channels` | This client's subscriptions: id, name, type, object count, persisted/locked. |
| `net channel <name>` | Its objects as this client has them: id, type, unique name, **label** (`NetInspector.Labeler`), `[persisted, client-auth, locked, dirty <fields>]`. |
| `net entity <ref> [field]` | Every replicated field of this client's copy, one per line as JSON (cut at 200 chars; name a field for all of it). |
| `net sync [target] [full]` | Compares this client's copies with fresh server snapshots: `all` (default), a channel (it and its objects), or one entity. Lists only what differs, per field: state, both seqs, and each differing path. |
| `net server channels [prefix]` | Every channel in the **server's** live memory: objects, subscribers (connection ids), idle time, lock holder. |
| `net server channel <name>` | A live channel's objects on the server, subscribed here or not: lock holder and waiters, owner, broadcast seq, `(not on this client)`. |
| `net server entity <ref> [field]` | The server's copy, decoded with this client's types, each field with its last change: `(seq 14, 3.2s ago by tcp_x)`. |
| `net server conns` | The server's connections (yours marked): local/remote, UDP, subscriptions, locks held, owned entities. |

**`<ref>`** is an entity id, `me` (`NetInspector.Me`), a subscribed channel's name, or an object's
unique name (`NetInspector.CurrentChannel`'s first when several channels have one). Server queries
are a round trip; they answer through `Cons.Defer`, so `tools/ucmd game net …` returns them in the
same call. They work against a local server and over a remote connection alike.

**Sync states** (Impunity's `FieldSyncState`, classified by sequence numbers, so a change on its way
isn't called a desync): `Mismatch` = both sides saw the same change and disagree — **a desync**;
`InFlightToClient` = the server changed it after this client's last update for it (re-check; if it
sticks, an unguaranteed update was lost); `InFlightToServer` = written here and not applied by the
server yet; `ChangedSinceSnapshot` = this client got a later change than the snapshot (re-check).
Plus `LocalProjection`: a field every client writes with `SetLocalOnly` **by design**, as the game
declares through `NetInspector.LocalProjection`. Those don't count as out of sync and only show
with `full`.

Caveats: a serializer's `ToBsonValue` is what the dump and the comparison see — one that returns
null hides its field from both. A server snapshot of an entity of a type this client hasn't
registered can't be decoded. `ImpunityOptions.AllowStateInspection` (default on) turns the server
queries off.

Verified 2026-11-14 in TestGameScene against a `LocalGameConnection` to a local `GameStateServer`
with the legacy test types (`TestZone` channel + a `TestPlayer`): every verb above answered, and
`net sync` / `dev net_sync` reported 0 out of sync.

---

## Authoring UI and prefabs

**Never hand-write `.prefab` YAML.** Build prefabs with a C# builder script run in the live editor
through `script run`. (`Assets/Prefabs/DevTools.prefab` and its placement in TestGameScene were made
this way.)

### Why not the declarative commands

| Command | Limitation |
|---|---|
| `prefab save_contents` | Despite "apply a declarative edit", it can only **rename a child** or **toggle one child active**. |
| `gameobject create` / `component add` / `component set_properties` | Work on **scene** objects, one call per property. Fine for a tweak; far too chatty for a hierarchy. |
| `prefab create` | Saves a scene GameObject as a prefab. Usable, but you still have to build the object first. |

A builder script does the whole thing in one call with full editor APIs, and stays re-runnable while
you iterate on the layout.

### The recipe

1. **Survey what exists.** Dump the prefabs you're matching (names, RectTransform anchors/pivot/
   size, components) with a short `script eval --file` script that loads the asset with
   `AssetDatabase.LoadAssetAtPath<GameObject>` and walks it.
2. **Write the builder** outside `Assets/` (a scratch directory works). No asset import, no domain
   reload:
   ```csharp
   using UnityEditor; using UnityEditor.SceneManagement;
   using UnityEngine; using UnityEngine.SceneManagement; using UnityEngine.UI;

   public static class BuildThing
   {
   	public static string Main()   // the single public static method is the entry point
   	{
   		Scene scene = EditorSceneManager.NewPreviewScene();   // keeps the open scene clean
   		try
   		{
   			var root = new GameObject("ThingDialog", typeof(RectTransform));
   			SceneManager.MoveGameObjectToScene(root, scene);   // children: SetParent(parent, false)
   			root.layer = LayerMask.NameToLayer("UI");
   			// Nested prefab instances: PrefabUtility.InstantiatePrefab(slotPrefab, parent)
   			PrefabUtility.SaveAsPrefabAsset(root, "Assets/Prefabs/ThingDialog.prefab");
   			return "saved";
   		}
   		finally { EditorSceneManager.ClosePreviewScene(scene); }
   	}
   }
   ```
3. **Compile-check, then run:**
   ```bash
   tools/ucmd script run --file /abs/path/BuildThing.cs --dry-run true
   tools/ucmd script run --file /abs/path/BuildThing.cs --timeout 60   # seconds
   ```
4. **Wire references into other prefabs or scenes** in the same script: `PrefabUtility.LoadPrefabContents`
   → assign → `SaveAsPrefabAsset` → `UnloadPrefabContents` in a `finally`; for a scene,
   `EditorSceneManager.OpenScene` → change → `SaveScene`.
5. **Verify.** Re-run the hierarchy dump against the saved prefab. Then `play`, open the UI through
   its real path, check laid-out rects with `ui dump` (`covered-by=` / `hidden`) or `script eval`
   (`Canvas.ForceUpdateCanvases()`, then `RectTransform.GetWorldCorners`), `shot`, and read
   `logs --since`.

### Prefab traps

- **One `Selectable` per GameObject.** A second `AddComponent<SomeButton>()` on an object that
  already has a `Button` returns **null**. To convert a cloned button to a subclass, retarget the
  script instead, which keeps every serialized field:
  ```csharp
  var so = new SerializedObject(go.GetComponent<Button>());
  so.FindProperty("m_Script").objectReferenceValue =
  	AssetDatabase.LoadAssetAtPath<MonoScript>("Assets/Scripts/UI/MyButton.cs");
  so.ApplyModifiedPropertiesWithoutUndo();
  ```
- **Edit-time `OnEnable` runs.** `Selectable`s execute in edit mode, so building one runs its
  `OnEnable` in the editor.
- **Re-running a builder overwrites the prefab.** Hand edits made in the editor since the last run
  are lost (the GUID survives, so references hold). Once a human refines a prefab by hand, switch to
  targeted edit scripts or retire the builder.
- **Modal layout:** a stretched root → `Backdrop` (stretched, first sibling) + `Window`. The
  backdrop must be a sibling of the window, never its parent, or clicks on the window bubble up to it.

---

## Traps

- **Reload gap.** Every domain reload takes the server down for ~2s. Raw `unity command` calls fail
  with exit 12 or exit 6 "socket hang up" — `ucmd` retries.
- **`script recompile_status` reports `completed` before the domain reload runs.** Wait for
  `editor status` to settle afterwards (`ucmd compile` does).
- **Paths that resolve under `Assets/`.** Authoring commands (`asset create`, `scene create`, …)
  resolve relative paths against `Assets/`. Captures and `script run --file` are project-root
  relative; absolute paths work.
- **`script eval` code is a method body.** No `using` directives; fully qualify types; call extension
  methods in static form.
- **`script run` sees the project's assemblies**, so a scratch script can drive game code directly —
  e.g. stand up a `GameStateServer` + `LocalGameConnection` in play mode, pump it from
  `Application.onBeforeRender`, and point `NetInspector.ConnectionSource` at it. Throwaway only.
- **A file eval can time out on its first compile** ("Main thread operation timed out after
  5000ms"). Retry, or pass `--timeout <seconds>`.
- **Never let a shell command read stdin** in a `ucmd` pipeline. It hangs until the tool timeout.
- **Take a log cursor before the work you're verifying** and read `logs --since` it.
- **Modal dialogs stall everything.** A modal (a menu item that pops a dialog, a crash reporter)
  blocks the main thread until a human dismisses it. `unity command` fails at once naming the
  dialog, and `unity status` reports it (`STATUS_BLOCKED_BY_DIALOG`).
- **A locked screen freezes the editor.** With the Mac's screen locked, the main thread blocks in its
  next repaint, so every main-thread command times out until someone unlocks. Check with
  `ioreg -l -d1 -n Root | grep CGSSessionScreenIsLocked`.
- **Safe Mode.** With compile errors at *editor launch*, Unity boots into Safe Mode and the Pipeline
  package doesn't load. `unity status` reports it (`STATUS_SAFE_MODE`); fix the errors from source
  and restart Unity (see [Restarting the editor](#restarting-the-editor)).
- **`Screen.width`/`height` lie outside the game's frame.** Read from a Pipeline command or a
  `script eval`, they report a different size than the game view renders at. `UiProbe.ScreenSize`
  uses the DevTools overlay canvas's `renderingDisplaySize` instead.
- **`ucmd compile` may say "up to date" after a stop.** Leaving play mode compiles pending edits
  itself. The edits are live; check with `ucmd list <new command>` or a test run.
- **Stale compile errors in `logs`.** The console buffer keeps the errors of a failed compile after
  a later one succeeds; trust `ucmd compile`'s exit code, and take cursors after it.
- **DontDestroyOnLoad objects** (DevTools, its overlay) don't appear in `tree`/`scene get_hierarchy`;
  reach them with `gameobject find` or `script eval`.
- **Quitting the editor:** `editor quit` refuses while scenes are unsaved unless `--save` or
  `--discard`; `unity close` does the same from outside.

---

## Player builds

**Desktop development builds** can host the same Pipeline server (`ucmd --runtime <Game.app>`), but
it isn't switched on yet in this project. To enable it: Project Settings ▸ Pipeline ▸ Runtime ▸
Enable in Development Builds **on** (it adds the `ENABLE_RUNTIME_PIPELINE_DEV` scripting define)
and `autoStart` **off** — with autoStart on, every editor play session would also start a second
(runtime) server and write its port file into the project root. `RuntimePipelineStarter`
(`DevTools.Cli`) starts the server in builds instead. Against a player, `status` (`runtime status`
+ `dev status`), `game`, `logs`, `shot` (via `dev shot`), `wait ingame`, `list` and raw commands
work; editor verbs refuse. Never enable it for release builds: it serves `eval`. It listens on
loopback only.

**WebGL builds** can't host it: a browser page can't open a listening socket, so there's no
Pipeline runtime server in a WebGL player (unverified against the package; the runtime server is
an in-process HTTP listener). Driving a WebGL build needs another route — e.g. the in-app browser
pane's JavaScript tool calling into the page (`SendMessage` into Unity / a `.jslib` hook that runs a
console line through `Cons.RunCommandCapturingOutput`), or the standalone server's own inspection.
Not built yet.

### Multiplayer harness (not ported yet)

Brambleville's `tools/mp` ran two-player tests on one machine: the editor hosts (play mode), a
**Mono development build** of the same code joins over TCP, both driven through Pipeline
(`mp up` / `mp client game …` / `mp shots` / `mp down`), with a source fingerprint to rebuild the
client only when stale and an APFS clone of the `.app` per client (the Pipeline port file lives
inside the bundle). It was built around Brambleville's own save/character flow, so it stays there.
A version for this project would start the `ImpunityStandaloneServer`, launch the editor and a dev
build (or a WebGL page in the browser pane) as clients, and wait on `dev status` `inGame`.

---

## After upgrading the CLI or Pipeline

Every bump from 0.7 to 1.0 broke `ucmd` somewhere: renamed commands, a dropped JSON envelope,
kebab-case parameters, new exit codes, list replies streamed as records. After any upgrade:

1. Read the changelogs for removed commands and changed replies; `grep` the docs and `tools/` for
   each name that went away.
2. `tools/ucmd compile` (our `[CliCommand]`s and `RuntimePipelineStarter` compile against the
   package), then `tools/ucmd tests editmode DevTools.Tests`, plus one run with a filter that
   matches nothing (exit 8).
3. `ucmd play`, `game help`, `game nosuchcmd` (exit 1), `shot`, `tree --depth 1`, `logs --since`,
   `script eval` with a bad statement, `stop`.
4. Update the version lines here. (Brambleville's copy of `ucmd` needs the same fixes.)

---

## CLI extras worth knowing

Plain `unity` commands (not `ucmd`), as of 1.0.0:

| Need | Command |
|---|---|
| Is the editor up / why not | `unity status` (`--json`): ready / starting / Safe Mode / blocked by a named dialog. `unity status --until-ready --timeout 120` blocks until it's ready. |
| Stream the console live | `unity logs --console --follow --level warn` (`--format ndjson` for machines). |
| Package manuals offline | `unity docs --package com.unity.inputsystem --content` prints the manual for the version in `packages-lock.json`. |
| Scripting API pages | `unity docs Tilemap.SetTile --content` prints the page as Markdown, pinned to the project's Unity version. |
| A command's parameters | `unity command <group> <name> --help` (from the connected editor) |
| Full catalog incl. editor commands | `unity commands --include-editor` |

### Restarting the editor

```bash
unity close . --if-saved                        # exit only if no scene is unsaved (lists them otherwise)
unity close .                                   # exit WITHOUT saving; names the work it discarded
unity open . --until-ready --accept-upgrades    # launch and wait until Pipeline answers
```

`unity open` on an already-open project is a no-op. `--until-ready` fails fast with
`OPEN_BLOCKED_BY_DIALOG_SAFE_MODE` (quoting the compile errors) instead of hanging on the Safe Mode
prompt. **Plain `close` discards unsaved scene/prefab edits**, so prefer `--if-saved` and ask
before closing an editor a human is using.

**MCP.** `unity mcp configure claude-code` would expose the commands as MCP tools
(`--tool-groups`, `--exclude-annotations destructive`). Not configured — the CLI via `ucmd` is
cheaper on context, scriptable, and rides out domain reloads.
