using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using UnityEngine;

namespace Dreamwing.Cons
{
	public abstract class CommandNode
	{
		public string Name;
		public string Path;
		public string HelpText;

		// The help text's first sentence, for one-line listings
		public string Summary => Commander.SummaryOf(HelpText);
	}

	public class ConsoleCommandGroup : CommandNode
	{
		public Dictionary<string, CommandNode> SubCommands = new Dictionary<string, CommandNode>();

		// Run when the line names the group but none of its subcommands (like
		// `git stash` vs `git stash pop`): `inv` lists, `inv give ...` gives. Has
		// the group's own path. Null = naming the group alone shows its help.
		public ConsoleCommand DefaultCommand;

		public ConsoleCommandGroup(string name, string path, string helpText)
		{
			Name = name;
			Path = path;
			HelpText = helpText;
		}
	}

	public class ConsoleCommand : CommandNode
	{
		public Delegate CommandAction;
		public CommandArgument[] Arguments;

		public ConsoleCommand(string name, string path, Delegate action, CommandArgument[] arguments, string helpText)
		{
			Name = name;
			Path = path;
			CommandAction = action;
			Arguments = arguments;
			HelpText = helpText;
		}

		public string GetUsageString()
		{
			return Path + ArgumentsUsage();
		}

		// " arg1 [arg2=default] ...", or "" without arguments
		public string ArgumentsUsage()
		{
			string usage = "";

			if (Arguments != null)
			{
				foreach (CommandArgument arg in Arguments)
				{
					usage += " " + arg.UsageString();
				}
			}

			return usage;
		}
	}

	public abstract class CommandArgument
	{
		public string Name;
		public object DefaultValue;

		public CommandArgument(string name, object defaultValue)
		{
			Name = name;
			DefaultValue = defaultValue;
		}

		public abstract object Parse(string arg);

		public string UsageString()
		{
			string usage = Name + ArgTypeString();

			if (DefaultValue != null)
			{
				usage = "[" + usage + "=" + DefaultValue + "]";
			}

			return usage;
		}

		public abstract string ArgTypeString();
		public abstract void Validate();

		// Values Tab can complete this argument to, or null when it's free-form
		public virtual IEnumerable<string> GetCompletions()
		{
			return null;
		}
	}

	public class CommandStringArgument : CommandArgument
	{
		public string[] PossibleValues;

		// Values Tab offers for a free-form argument (item ids, area ids, ...), looked
		// up when completing; unlike PossibleValues they aren't enforced
		public Func<IEnumerable<string>> Completer;

		public CommandStringArgument(string name, string defaultValue = null) : base(name, defaultValue)
		{

		}

		public override object Parse(string arg)
		{
			if (PossibleValues != null)
			{
				if (!PossibleValues.Contains(arg))
				{
					throw new Exception("Argument \"" + arg + "\" not a valid option");
				}
			}

			return arg;
		}

		public override string ArgTypeString()
		{
			if (PossibleValues == null)
				return "";

			return "(" + String.Join("/", PossibleValues) + ")";
		}

		public override void Validate()
		{
			if (PossibleValues != null && PossibleValues.Length == 0)
				throw new Exception("String argument with no possible values");
		}

		public override IEnumerable<string> GetCompletions()
		{
			if (PossibleValues != null)
				return PossibleValues;

			try
			{
				return Completer?.Invoke();
			}
			catch (Exception)
			{
				// No game loaded, catalog not built, ...: just nothing to offer
				return null;
			}
		}

	}

	public class CommandIntArgument : CommandArgument
	{
		public int? MinValue;
		public int? MaxValue;

		public CommandIntArgument(string name, int? defaultValue = null, int? minValue = null, int? maxValue = null) : base(name, defaultValue)
		{
			MinValue = minValue;
			MaxValue = maxValue;
		}

		public override object Parse(string arg)
		{
			if (!Int32.TryParse(arg, out int intValue))
				throw new Exception("Argument \"" + arg + "\" does not appear to be a valid integer");

			if (MinValue.HasValue && intValue < MinValue.Value)
				throw new Exception(Name + " must not be less than " + MinValue.Value);
			if (MaxValue.HasValue && intValue > MaxValue.Value)
				throw new Exception(Name + " must not be more than " + MaxValue.Value);

			return intValue;
		}

		public override string ArgTypeString()
		{
			return "(int)";
		}

		public override void Validate()
		{
			if (MinValue.HasValue && MaxValue.HasValue &&
				MinValue.Value > MaxValue.Value)
				throw new Exception("Min value is larger than max value");
		}
	}

	public class CommandFloatArgument : CommandArgument
	{
		public float? MinValue;
		public float? MaxValue;

		public CommandFloatArgument(string name, float? defaultValue = null) : base(name, defaultValue)
		{

		}

		public override object Parse(string arg)
		{
			if (!Single.TryParse(arg, out float floatValue))
				throw new Exception("Argument \"" + arg + "\" does not appear to be a valid integer");

			if (MinValue.HasValue && floatValue < MinValue.Value)
				throw new Exception(Name + " must not be less than " + MinValue.Value);
			if (MaxValue.HasValue && floatValue > MaxValue.Value)
				throw new Exception(Name + " must not be more than " + MaxValue.Value);

			return floatValue;
		}

		public override string ArgTypeString()
		{
			return "(float)";
		}

		public override void Validate()
		{
			if (MinValue.HasValue && MaxValue.HasValue &&
				MinValue.Value > MaxValue.Value)
				throw new Exception("Min value is larger than max value");
		}
	}

	public class CommandBoolArgument : CommandArgument
	{
		public CommandBoolArgument(string name, bool? defaultValue = null) : base(name, defaultValue)
		{

		}

		public override object Parse(string arg)
		{
			arg = arg.ToLower();
			if (arg == "t" || arg == "true" || arg == "yes" || arg == "y")
				return true;
			else if (arg == "f" || arg == "false" || arg == "no" || arg == "n")
				return false;

			throw new Exception("Argument \"" + arg + "\" does not appear to be a valid boolean value");
		}

		public override string ArgTypeString()
		{
			return "(y/n)";
		}

		public override void Validate()
		{

		}

		public override IEnumerable<string> GetCompletions()
		{
			return new[] { "true", "false" };
		}
	}

	public class CommandRemainingArgument : CommandArgument
	{
		public CommandRemainingArgument(string name) : base(name, null)
		{ }

		public override object Parse(string arg)
		{
			return null;
		}

		public override string ArgTypeString()
		{
			return "...";
		}

		public override void Validate()
		{
			if (DefaultValue != null)
				throw new Exception("Remaining args can't have default");
		}
	}

	// The rest of the line is a command path (as for help); Tab completes it
	// as one
	public class CommandPathArgument : CommandRemainingArgument
	{
		public CommandPathArgument(string name) : base(name)
		{ }
	}

	public class CommandCompletion
	{
		// The line with its last word completed or extended; unchanged when there
		// was nothing to add
		public string Line;

		// Set when the last word is ambiguous and can't be extended any further:
		// the choices to show
		public List<string> Candidates;
	}

	// Where the commander's own messages go (help, unknown commands, argument
	// errors). Normally the console panel; tests record them instead.
	public interface ICommandOutput
	{
		void Log(string message);
		void LogWarning(string message);

		// Two columns, the second lined up: a name and a line about it
		void LogRows(IList<(string Name, string Text)> rows);
	}

	public class ConsoleCommandOutput : ICommandOutput
	{
		public void Log(string message) => Cons.Log(message);
		public void LogWarning(string message) => Cons.LogWarning(message);
		public void LogRows(IList<(string Name, string Text)> rows) => Cons.LogRows(rows);
	}


	// The command tree. Groups hold commands and further groups at any depth
	// ("inv give", "inventory catalog rebuild"), and a group can have a default
	// command of its own (see ConsoleCommandGroup.DefaultCommand). Names are
	// lowercase; see docs/DevConsole.md for the naming conventions.
	public class Commander
	{
		private const int MaxSuggestions = 5;

		private ConsoleCommandGroup RootCommands = new ConsoleCommandGroup("", "", "Top level commands");

		public ICommandOutput Output = new ConsoleCommandOutput();

		public void AddCommandGroup(string groupPath, string helpText)
		{
			string[] path = SplitPath(groupPath);
			ConsoleCommandGroup parent = FindParentGroup(path);

			string groupName = path[path.Length - 1];
			if (parent.SubCommands.TryGetValue(groupName, out CommandNode existing))
			{
				if (existing is ConsoleCommand)
					throw new Exception("Group conflicts with command name: " + groupName +
						" (to give a group a default command, add the group first)");
				throw new Exception("Group name conflict: " + groupName);
			}

			parent.SubCommands.Add(groupName, new ConsoleCommandGroup(groupName, String.Join(" ", path), helpText));
		}

		// A path naming an existing group makes this the group's default command
		public void AddCommand(string commandPath, Delegate action, string helpText, CommandArgument[] arguments)
		{
			ValidateArguments(arguments);

			string[] path = SplitPath(commandPath);
			ConsoleCommandGroup parent = FindParentGroup(path);

			string commandName = path[path.Length - 1];
			ConsoleCommand command = new ConsoleCommand(commandName, String.Join(" ", path), action, arguments, helpText);

			if (!parent.SubCommands.TryGetValue(commandName, out CommandNode existing))
			{
				CheckDefaultArgumentsAllow(parent, commandName);
				parent.SubCommands.Add(commandName, command);
				return;
			}

			ConsoleCommandGroup group = existing as ConsoleCommandGroup;
			if (group == null)
				throw new Exception("Command name conflict: " + commandName);
			if (group.DefaultCommand != null)
				throw new Exception("Group already has a default command: " + command.Path);

			group.DefaultCommand = command;
			foreach (string child in group.SubCommands.Keys)
				CheckDefaultArgumentsAllow(group, child);
		}

		private static string[] SplitPath(string commandPath)
		{
			string[] path = commandPath.Trim().ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
			if (path.Length == 0)
				throw new Exception("Empty command path");
			return path;
		}

		// The group holding the last name in path
		private ConsoleCommandGroup FindParentGroup(string[] path)
		{
			ConsoleCommandGroup group = RootCommands;
			for (int c = 0; c < path.Length - 1; c++)
			{
				if (!group.SubCommands.TryGetValue(path[c], out CommandNode node))
					throw new Exception("Command subgroup not created " + path[c]);

				group = node as ConsoleCommandGroup;
				if (group == null)
					throw new Exception("Command group conflicts with command name: " + path[c]);
			}

			return group;
		}

		// A word naming a subcommand always runs the subcommand, so a value of the
		// default command's first argument that's also a subcommand name could never
		// reach it
		private static void CheckDefaultArgumentsAllow(ConsoleCommandGroup group, string childName)
		{
			CommandArgument[] arguments = group.DefaultCommand?.Arguments;
			if (arguments == null || arguments.Length == 0)
				return;

			if (arguments[0] is CommandStringArgument first && first.PossibleValues != null &&
				first.PossibleValues.Contains(childName, StringComparer.OrdinalIgnoreCase))
				throw new Exception("\"" + childName + "\" is both a value of " + group.Path + "'s " +
					first.Name + " argument and a subcommand name");
		}

		private void ValidateArguments(CommandArgument[] arguments)
		{
			if (arguments == null)
				return;

			bool inDefaults = false;
			bool gotRemaining = false;
			foreach (CommandArgument arg in arguments)
			{
				arg.Validate();

				if (arg is CommandRemainingArgument)
				{
					if (gotRemaining)
						throw new Exception("Can't have a two remaining list arguments");
					if (inDefaults)
						throw new Exception("Can't have a remaining list argument after an optional argument");

					gotRemaining = true;
				}
				else if (gotRemaining)
				{
					throw new Exception("Can't have more arguments after a remaining list");
				}

				if (arg.DefaultValue != null && !inDefaults)
				{
					inDefaults = true;
				}
				else if (arg.DefaultValue == null && inDefaults)
				{
					throw new Exception("Can't have a non-optional argument after an optional one");
				}
			}
		}


		// False when the line didn't run cleanly: it didn't parse, named no command, had bad
		// arguments, or the command threw. Each of those is already reported on Output; the
		// result is for callers outside the panel (dev tooling) that need a verdict. A command
		// that refuses by logging an error still returns true here — Cons watches for that.
		public bool Process(string commandLine)
		{
			commandLine = commandLine.Trim();
			if (String.IsNullOrEmpty(commandLine))
				return true;

			List<string> tokens = null;
			try
			{
				tokens = CommandTokenizer.Tokenize(commandLine);
			}
			catch (Exception e)
			{
				Output.LogWarning("Error parsing command: " + e.Message);
				return false;
			}

			return RunCommand(tokens);
		}

		public CommandNode GetCommandInfo(string commandPath)
		{
			string[] path = commandPath.Trim().ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

			CommandNode node = RootCommands;
			foreach (string name in path)
			{
				ConsoleCommandGroup group = node as ConsoleCommandGroup;
				if (group == null || !group.SubCommands.TryGetValue(name, out node))
					return null;
			}

			return node;
		}

		private bool RunCommand(List<string> argv)
		{
			ConsoleCommandGroup group = RootCommands;

			for (int pos = 0; pos < argv.Count; pos++)
			{
				// Command names are registered lowercase (phone keyboards like to
				// capitalize the first word); arguments keep their case
				if (group.SubCommands.TryGetValue(argv[pos].ToLowerInvariant(), out CommandNode node))
				{
					if (node is ConsoleCommand command)
						return RunCommand(command, argv.GetRange(pos + 1, argv.Count - pos - 1)) == RunOutcome.Ran;

					group = (ConsoleCommandGroup)node;
					continue;
				}

				// Not a subcommand: a default command that takes arguments gets the
				// rest of the line. One that takes none can't, so the word is a
				// mistyped subcommand.
				ConsoleCommand groupDefault = group.DefaultCommand;
				if (groupDefault?.Arguments != null && groupDefault.Arguments.Length > 0)
				{
					RunOutcome outcome = RunCommand(groupDefault, argv.GetRange(pos, argv.Count - pos));
					if (outcome == RunOutcome.BadArguments)
						LogSuggestions(argv);
					return outcome == RunOutcome.Ran;
				}

				Output.LogWarning("Unknown command: " + String.Join(" ", argv.GetRange(0, pos + 1)));
				if (!LogSuggestions(argv))
					Output.Log(group == RootCommands ? "(help lists the commands)" : "(help " + group.Path + " lists its commands)");
				return false;
			}

			if (group.DefaultCommand != null)
				return RunCommand(group.DefaultCommand, new List<string>()) == RunOutcome.Ran;

			ShowHelp(group);
			return true;
		}

		private enum RunOutcome { Ran, BadArguments, Threw }

		// Every failure is already logged when this returns
		private RunOutcome RunCommand(ConsoleCommand command, List<string> argv)
		{
			object[] args = ParseArguments(command, argv);
			if (args == null)
				return RunOutcome.BadArguments;

			// A throwing command is reported here rather than escaping into whoever
			// submitted the line (the input field's key handling, or dev tooling)
			try
			{
				command.CommandAction.DynamicInvoke(args);
			}
			catch (TargetInvocationException e)
			{
				// Through Unity's log so it reaches the editor console / player log
				// too; the interceptor echoes it to the panel
				Debug.LogException(e.InnerException ?? e);
				return RunOutcome.Threw;
			}

			return RunOutcome.Ran;
		}

		private object[] ParseArguments(ConsoleCommand command, List<string> argv)
		{
			CommandArgument[] arguments = command.Arguments ?? new CommandArgument[0];
			object[] args = new object[arguments.Length];

			try
			{
				for (int a = 0; a < arguments.Length; a++)
				{
					CommandArgument arg = arguments[a];

					CommandRemainingArgument remaining = arg as CommandRemainingArgument;
					if (remaining != null)
					{
						int numRemaining = argv.Count - a;
						string[] remainingList = new string[numRemaining];

						int i = 0;
						for (int r = a; r < argv.Count; r++)
						{
							remainingList[i++] = argv[r];
						}

						args[a] = remainingList;
						return args;
					}

					if (a < argv.Count)
					{
						args[a] = arg.Parse(argv[a]);
					}
					else
					{
						if (arg.DefaultValue != null)
						{
							args[a] = arg.DefaultValue;
						}
						else
						{
							throw new Exception("Expecting argument " + arg.Name);
						}
					}
				}

				// Leftovers are almost always a typo or a misremembered signature;
				// running with them silently dropped would hide that
				if (argv.Count > arguments.Length)
					throw new Exception("Too many arguments, starting at \"" + argv[arguments.Length] + "\"");
			}
			catch (Exception e)
			{
				Output.LogWarning("Argument error: " + e.Message);
				Output.Log("Usage: " + command.GetUsageString());
				return null;
			}

			return args;
		}

		// ---- did you mean ------------------------------------------------------

		// For a line whose path breaks off at an unknown word: commands and groups
		// anywhere in the tree with exactly that name (a command typed without its
		// group, `give` for `inv give`), then near misses among the names that
		// could have gone there. Full paths; empty when the whole line resolves or
		// nothing is close.
		public List<string> Suggest(List<string> argv)
		{
			List<string> suggestions = new List<string>();

			ConsoleCommandGroup group = RootCommands;
			string unknown = null;
			foreach (string arg in argv)
			{
				string word = arg.ToLowerInvariant();
				if (!group.SubCommands.TryGetValue(word, out CommandNode node))
				{
					unknown = word;
					break;
				}

				group = node as ConsoleCommandGroup;
				if (group == null)
					return suggestions;
			}

			if (unknown == null)
				return suggestions;

			// An exact name is the likelier intent, so it goes first
			foreach (CommandNode node in AllNodes(RootCommands))
			{
				if (node.Name == unknown)
					suggestions.Add(node.Path);
			}

			foreach (CommandNode sibling in group.SubCommands.Values.OrderBy(n => n.Name, StringComparer.Ordinal))
			{
				if (IsNearMiss(unknown, sibling.Name) && !suggestions.Contains(sibling.Path))
					suggestions.Add(sibling.Path);
			}

			if (suggestions.Count > MaxSuggestions)
				suggestions.RemoveRange(MaxSuggestions, suggestions.Count - MaxSuggestions);
			return suggestions;
		}

		private bool LogSuggestions(List<string> argv)
		{
			List<string> suggestions = Suggest(argv);
			if (suggestions.Count == 0)
				return false;

			Output.Log("Did you mean: " + String.Join(", ", suggestions) + "?");
			return true;
		}

		// A prefix either way ("inv" / "invent"), or a small edit distance
		// ("gve"), tighter for short words so "give" doesn't match "game"
		private static bool IsNearMiss(string typed, string name)
		{
			if (name.StartsWith(typed, StringComparison.Ordinal) || typed.StartsWith(name, StringComparison.Ordinal))
				return true;

			int allowed = typed.Length <= 4 ? 1 : 2;
			return EditDistance(typed, name) <= allowed;
		}

		private static int EditDistance(string a, string b)
		{
			int[] previous = new int[b.Length + 1];
			int[] current = new int[b.Length + 1];
			for (int j = 0; j <= b.Length; j++)
				previous[j] = j;

			for (int i = 1; i <= a.Length; i++)
			{
				current[0] = i;
				for (int j = 1; j <= b.Length; j++)
				{
					int substitute = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
					current[j] = Math.Min(substitute, Math.Min(previous[j], current[j - 1]) + 1);
				}

				int[] swap = previous;
				previous = current;
				current = swap;
			}

			return previous[b.Length];
		}

		// Every node under group, depth first, each level by name
		private static IEnumerable<CommandNode> AllNodes(ConsoleCommandGroup group)
		{
			foreach (CommandNode node in group.SubCommands.Values.OrderBy(n => n.Name, StringComparer.Ordinal))
			{
				yield return node;

				if (node is ConsoleCommandGroup sub)
				{
					foreach (CommandNode descendant in AllNodes(sub))
						yield return descendant;
				}
			}
		}

		// ---- help --------------------------------------------------------------

		// help [path...]         a command's usage, or a group's listing
		// help --all [path...]   the whole tree under path, one line per node
		// help --find <text>     nodes whose path or help text contains text
		public void Help(string[] args)
		{
			string flag = args.Length > 0 && args[0].StartsWith("-") ? args[0] : null;
			string[] rest = flag == null ? args : args.Skip(1).ToArray();

			if (flag == "--find" || flag == "-f")
			{
				if (rest.Length == 0)
				{
					Output.LogWarning("Usage: help --find <text>");
					return;
				}

				List<CommandNode> found = Find(String.Join(" ", rest));
				if (found.Count == 0)
					Output.Log("Nothing matches \"" + String.Join(" ", rest) + "\"");
				foreach (CommandNode node in found)
					Output.Log(TreeLine(node, node.Path));
				return;
			}

			if (flag != null && flag != "--all" && flag != "-a")
			{
				Output.LogWarning("Unknown help option " + flag + " (--all, --find)");
				return;
			}

			string path = String.Join(" ", rest);
			CommandNode target = GetCommandInfo(path);
			if (target == null)
			{
				Output.LogWarning("Command not found: " + path);
				LogSuggestions(rest.ToList());
				return;
			}

			if (flag == null)
			{
				ShowHelp(target);
				return;
			}

			foreach (string line in TreeLines(target))
				Output.Log(line);
		}

		public void ShowHelp(CommandNode node)
		{
			if (node is ConsoleCommand command)
			{
				Output.Log(command.HelpText);
				Output.Log("Usage: " + command.GetUsageString());
				return;
			}

			ConsoleCommandGroup group = (ConsoleCommandGroup)node;
			if (group == RootCommands)
				Output.Log("help <command> for details, help --all for everything, help --find <text> to search");
			else
				Output.Log(group.Path + ": " + group.HelpText);

			if (group.DefaultCommand != null)
				Output.Log("Alone: " + group.DefaultCommand.GetUsageString() + "  " + group.DefaultCommand.Summary);

			(List<(string Name, string Text)> groups, List<(string Name, string Text)> commands) = DescribeGroup(group);
			if (groups.Count > 0)
			{
				Output.Log("Groups:");
				Output.LogRows(groups);
			}

			if (commands.Count > 0)
			{
				Output.Log("Commands:");
				Output.LogRows(commands);
			}
		}

		// A group's children as listing rows (name, summary), subgroups apart from
		// commands, each by name
		public (List<(string Name, string Text)> Groups, List<(string Name, string Text)> Commands) DescribeGroup(ConsoleCommandGroup group)
		{
			List<(string Name, string Text)> groups = new List<(string Name, string Text)>();
			List<(string Name, string Text)> commands = new List<(string Name, string Text)>();

			foreach (CommandNode node in group.SubCommands.Values.OrderBy(n => n.Name, StringComparer.Ordinal))
			{
				if (node is ConsoleCommandGroup)
					groups.Add((node.Name, node.Summary));
				else
					commands.Add((node.Name, node.Summary));
			}

			return (groups, commands);
		}

		// The tree under node (or the whole tree), one line per node, indented by
		// depth: name, arguments, summary
		public List<string> TreeLines(CommandNode node)
		{
			List<string> lines = new List<string>();
			if (node == RootCommands)
			{
				foreach (CommandNode child in RootCommands.SubCommands.Values.OrderBy(n => n.Name, StringComparer.Ordinal))
					AddTreeLines(child, child.Name, "", lines);
			}
			else
			{
				AddTreeLines(node, node.Path, "", lines);
			}

			return lines;
		}

		private static void AddTreeLines(CommandNode node, string label, string indent, List<string> lines)
		{
			lines.Add(indent + TreeLine(node, label));

			if (node is ConsoleCommandGroup group)
			{
				foreach (CommandNode child in group.SubCommands.Values.OrderBy(n => n.Name, StringComparer.Ordinal))
					AddTreeLines(child, child.Name, indent + "  ", lines);
			}
		}

		private static string TreeLine(CommandNode node, string label)
		{
			if (node is ConsoleCommand command)
				return label + command.ArgumentsUsage() + " — " + command.Summary;

			ConsoleCommandGroup group = (ConsoleCommandGroup)node;
			string line = label + " … — " + group.Summary;
			if (group.DefaultCommand != null)
				line = label + group.DefaultCommand.ArgumentsUsage() + " … — " + group.Summary +
					"; alone: " + group.DefaultCommand.Summary;
			return line;
		}

		// Nodes whose path or help text (a group's default command's included)
		// contains text, ignoring case, in tree order
		public List<CommandNode> Find(string text)
		{
			List<CommandNode> found = new List<CommandNode>();
			foreach (CommandNode node in AllNodes(RootCommands))
			{
				if (Contains(node.Path, text) || Contains(node.HelpText, text) ||
					(node is ConsoleCommandGroup group && group.DefaultCommand != null && Contains(group.DefaultCommand.HelpText, text)))
					found.Add(node);
			}

			return found;
		}

		private static bool Contains(string value, string text)
		{
			return value != null && value.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
		}

		// The help text's first sentence: up to the first period that ends one
		// (followed by a space, outside parentheses, so "(e.g. Arms)" survives)
		public static string SummaryOf(string helpText)
		{
			if (String.IsNullOrEmpty(helpText))
				return "";

			int depth = 0;
			for (int i = 0; i < helpText.Length; i++)
			{
				char c = helpText[i];
				if (c == '(')
					depth++;
				else if (c == ')')
					depth = Math.Max(0, depth - 1);
				else if (c == '.' && depth == 0 && i + 1 < helpText.Length && helpText[i + 1] == ' ')
					return helpText.Substring(0, i);
			}

			return helpText.TrimEnd('.');
		}

		// ---- tab completion ----------------------------------------------------

		// Tab completion for a console line. The word after the last space is the
		// one completed: a command or group name, or an argument value where the
		// argument lists its values. A single match completes (plus a space), several
		// extend to their longest common prefix, and when that adds nothing they're
		// returned as candidates to show.
		public CommandCompletion Complete(string line)
		{
			CommandCompletion completion = new CommandCompletion { Line = line };

			int partialStart = line.Length;
			while (partialStart > 0 && !Char.IsWhiteSpace(line[partialStart - 1]))
				partialStart--;

			string head = line.Substring(0, partialStart);
			string partial = line.Substring(partialStart);

			List<string> words;
			try
			{
				words = CommandTokenizer.Tokenize(head.Trim());
			}
			catch (Exception)
			{
				// Inside an unclosed quote: nothing sensible to complete
				return completion;
			}

			List<string> candidates = CompletionsFor(words, 0, partial);
			if (candidates == null || candidates.Count == 0)
				return completion;

			if (candidates.Count == 1)
			{
				completion.Line = head + candidates[0] + " ";
				return completion;
			}

			string common = CommonPrefix(candidates);
			if (common.Length > partial.Length)
				completion.Line = head + common;
			else
				completion.Candidates = candidates;

			return completion;
		}

		// Completions for the word after words[start..], walking the command tree
		// from the root
		private List<string> CompletionsFor(List<string> words, int start, string partial)
		{
			ConsoleCommandGroup group = RootCommands;
			for (int w = start; w < words.Count; w++)
			{
				if (!group.SubCommands.TryGetValue(words[w].ToLowerInvariant(), out CommandNode node))
				{
					// Not a subcommand: from here on the words are the group's
					// default command's arguments
					return group.DefaultCommand == null ? null : ArgumentCompletions(group.DefaultCommand, words, w, partial);
				}

				if (node is ConsoleCommand command)
					return ArgumentCompletions(command, words, w + 1, partial);

				group = (ConsoleCommandGroup)node;
			}

			// Right after a group: its subcommands, and its default command's first
			// argument when it has one
			List<string> candidates = Matching(group.SubCommands.Keys, partial);
			if (group.DefaultCommand != null)
			{
				List<string> values = ArgumentCompletions(group.DefaultCommand, words, words.Count, partial);
				if (values != null)
				{
					foreach (string value in values)
					{
						if (!candidates.Contains(value))
							candidates.Add(value);
					}

					candidates.Sort(StringComparer.Ordinal);
				}
			}

			return candidates;
		}

		private List<string> ArgumentCompletions(ConsoleCommand command, List<string> words, int firstArg, string partial)
		{
			CommandArgument[] arguments = command.Arguments;
			if (arguments == null || arguments.Length == 0)
				return null;

			// A remaining-list argument (always last) takes every word from its
			// position on
			int index = words.Count - firstArg;
			CommandArgument last = arguments[arguments.Length - 1];
			if (index >= arguments.Length - 1 && last is CommandRemainingArgument)
				index = arguments.Length - 1;
			else if (index >= arguments.Length)
				return null;

			if (arguments[index] is CommandPathArgument)
			{
				// Options (help's --all) go before the path
				int pathStart = firstArg + index;
				while (pathStart < words.Count && words[pathStart].StartsWith("-"))
					pathStart++;

				return CompletionsFor(words, pathStart, partial);
			}

			return Matching(arguments[index].GetCompletions(), partial);
		}

		private static List<string> Matching(IEnumerable<string> values, string partial)
		{
			if (values == null)
				return null;

			List<string> matches = new List<string>();
			foreach (string value in values)
			{
				if (value.StartsWith(partial, StringComparison.OrdinalIgnoreCase) && !matches.Contains(value))
					matches.Add(value);
			}

			matches.Sort(StringComparer.Ordinal);
			return matches;
		}

		private static string CommonPrefix(List<string> values)
		{
			string first = values[0];
			int length = first.Length;
			foreach (string value in values)
			{
				int i = 0;
				while (i < length && i < value.Length && Char.ToLowerInvariant(value[i]) == Char.ToLowerInvariant(first[i]))
					i++;
				length = i;
			}

			return first.Substring(0, length);
		}
	}

}
