# Dev console

The in-game developer console (Dreamwing `Cons`, toggled with the backquote key) and the commands it
runs. Agents reach the same commands from the shell with `tools/ucmd game <command line>`
([AgentTooling.md](AgentTooling.md)). Ported from Brambleville on 2026-11-14.

Commands form a **tree of groups**, like `git` or `aws`: `net server entity 3`, `ui click OK`. A
group can hold commands and further groups at any depth, and the top level mixes the two (`help` and
`quit` are plain commands next to the groups).

**This doc doesn't list every command.** The running game is the reference:

```bash
tools/ucmd game help --all           # every command with its arguments, one line each
tools/ucmd game help --find sync     # search names and help text
tools/ucmd game help net             # one group's commands
```

## The groups

| Group | For | Registered by |
|---|---|---|
| `console` | The console itself: `clear`, `clear-history`, `close` | `BuiltinCommands` |
| `system` | Runtime diagnostics: `info`, `gc` | `BuiltinCommands` |
| `net` | Replicated-state inspection: `channels`, `channel`, `entity`, `sync`, and the `server` subgroup ([AgentTooling.md](AgentTooling.md#inspecting-replicated-state)) | `DevTools.NetCommands` |
| `ui` | Driving uGUI without a mouse: `dump`, `text`, `click`, `settext`, `toggle`, `clickat`; `fullscreen` ([AgentTooling.md](AgentTooling.md#driving-ui)) | `DevTools.UiCommands` |

A game adds its own groups (and may add verbs to `net` / `ui`, e.g. `net host`, `net join`) from its
startup code, after `DevToolsRoot.Awake` has run `Cons.Init()`.

## Running a line

`Commander.Process` tokenizes the line and walks the tree from the root, one word at a time:

- A word that names a **child** of the current group descends into it, or runs it if it's a command.
  The remaining words are that command's arguments. Command names match case-insensitively (phone
  keyboards capitalize the first word); arguments keep their case.
- At a group, a word that isn't a child goes to the group's **default command** if it has one that
  takes arguments. Otherwise it's an unknown command.
- A line that ends on a group runs the group's default command, or prints the group's help if it
  has none.
- **Unknown commands get suggestions** (`Commander.Suggest`). First come commands or groups anywhere
  in the tree with exactly that name (`sync` → `net sync`). Then come names in that group within a
  small edit distance or sharing a prefix (one edit for words up to four letters, two for longer).
- Arguments parse in order. Too few or too many, or a value that doesn't parse, prints the error and
  the full-path usage, and the command doesn't run. A command that throws is logged through
  `Debug.LogException` (so it reaches the Unity console), not rethrown.

### Groups with a default command

A group can run something of its own, like `git stash` vs `git stash pop`. Use one only for the
group's obvious "show me" action (list, status), never for something that changes state, because a
mistyped subcommand must not fall through into it.

## Registering commands

```csharp
public static void Register()
{
	Cons.AddCommandGroup("demo", "The demo's own verbs");          // the group first

	Cons.AddCommand("demo", ShowStatus,                              // optional: its default command
		"Shows the demo's connection and players");

	Cons.AddCommand<string, int>("demo connect", Connect,
		"Connects to a standalone server",
		new CommandStringArgument("host", "localhost"),
		new CommandIntArgument("port", 29653));
}
```

- **Paths are space-separated.** Every group on the path must already be registered, so a typo in
  a group name fails loudly at startup instead of creating a stray group.
- **Registration throws** (at startup) for: a name that's already taken; a group registered after a
  command with the same name (register the group first, then its default); a second default command
  for the same group; a default command whose first argument's `PossibleValues` include a subcommand
  name.
- **Arguments:** `CommandStringArgument`, `CommandIntArgument` (optional min/max),
  `CommandFloatArgument`, `CommandBoolArgument`, `CommandRemainingArgument` (the rest of the line),
  and `CommandPathArgument` (the rest of the line as a command path, as `help` takes). An argument
  with a default is optional, and only optional ones may follow it. `Cons.AddCommand` has typed
  overloads for up to four arguments.
- **Help text:** the first sentence (up to the first `. ` outside parentheses) is the one-line
  summary in listings, so make it stand alone. Detail goes after it and shows in `help <command>`
  and `help --all`.
- **Messages** start with the command's full path (`demo connect: refused …`). Output that arrives
  on a later frame goes through `Debug.Log` so it lands in the Unity console, where
  `tools/ucmd logs` reads it — unless the command answers through a deferred reply.
- **Answering on a later frame** (a server round trip, like `net server …` and `net sync`): call
  `Cons.Defer()` while the handler runs, log through the returned `ConsoleReply` (`Log`,
  `LogWarning`, `LogError`), then `Finish()` it (`Fail(message)` = error + finish). Typed into the
  panel, the lines just appear there. Run through `dev console` / `tools/ucmd game`
  (`Cons.RunCommandCapturingOutputAsync`), the call **waits** for every reply the line deferred, up
  to `dev console --timeout` (20 s), so the lines come back in the same call and an error in them
  fails it. The pattern (`NetCommands.Ask`): an `async void` that calls `Cons.Defer()` *before* its
  first `await`, with a `try`/`catch` that `Fail`s. Main thread only.
- **Refuse with `Cons.LogError`** (or `Debug.LogError`), not `Cons.Log`. A line **fails** when it
  doesn't parse, names no command, has bad arguments, throws, or logs an error while it runs.
  `tools/ucmd game` then exits 1 and `dev console` exits 6, still returning the output. Warnings
  don't count as failures.

### Naming

- Short, lowercase words. Use kebab-case only when one word won't do (`clear-history`).
- Group by **what's acted on**, then use a verb for the command (`set`, `clear`, `get`). Within a
  group, the same verb should mean the same thing.
- Put a command at the top level only if it's about the console or program itself.

## Help

| Form | Shows |
|---|---|
| `help` | The top level, groups and commands apart, one `name  summary` row each |
| `help <group…>` | The group's help text, its default command's usage if it has one, then its subgroups and commands |
| `help <command…>` | The full help text and usage line |
| `help --all [path…]` | The whole tree (or the subtree at path), indented, one line per node |
| `help --find <text>` | Every node whose path or help text contains the text, as full paths |

On the panel, rows are lined up with tab stops (`ColumnLayout.FormatRows`). Captured output
(`dev console`) is padded with spaces instead (`ColumnLayout.FormatRowsPlain`).

## Tab completion

`Commander.Complete` completes the last word of the line. One match completes it and adds a space.
Several matches extend to their longest common prefix, and if that adds nothing they're listed.

- After a group: its subcommand names, plus its default command's first-argument values.
- After a command: the values of the argument at that position — `PossibleValues` (enforced when
  the command runs), `true`/`false` for bools, or a `Completer` (`Func<IEnumerable<string>>`, only
  offered, never enforced; one that throws just offers nothing). `net channel` completes channel
  names.

## Code

| File | What |
|---|---|
| `Assets/Scripts/Dreamwing/Cons/Commander.cs` | The tree, registration checks, dispatch, `Suggest`, help, completion, argument types. Output goes through `ICommandOutput` (`Commander.Output`), so tests can record it. |
| `Assets/Scripts/Dreamwing/Cons/Cons.cs` | The panel, and the static facade the game registers through (`Init` — idempotent — / `AddCommandGroup` / `AddCommand` / `Help` / `RunCommandCapturingOutput(Async)` / `Defer` / `LogRows`). |
| `Assets/Scripts/Dreamwing/Cons/ConsoleReply.cs` | A deferred answer (`Cons.Defer`). |
| `Assets/Scripts/Dreamwing/Cons/BuiltinCommands.cs` | `help`, `quit`, `console`, `system`. |
| `Assets/Scripts/Dreamwing/Cons/ColumnLayout.cs` | Tab-stop layouts: the candidate grid and the two-column help rows. |
| `Assets/Scripts/Dreamwing/Cons/UnityDebugInterceptor.cs` | Mirrors Unity's log into the panel. |
| `Assets/Scripts/DevTools/DevToolsRoot.cs` | Bootstrap: `Cons.Init`, the generic groups, the overlay canvas + panel, open/close events. |
| `Assets/Tests/DevTools/` | `CommanderTests`, `ColumnLayoutTests`, `ConsoleReplyTests` (+ `NetInspectorTests`, `UiProbeTests`). |
