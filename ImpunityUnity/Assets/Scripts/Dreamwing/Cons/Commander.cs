using System.Linq;
using System;
using System.Collections.Generic;

namespace Dreamwing.Cons
{
	public abstract class CommandNode
	{
		public string Name;
		public string Path;
		public string HelpText;
	}

	public class ConsoleCommandGroup : CommandNode
	{
		public Dictionary<string, CommandNode> SubCommands = new Dictionary<string, CommandNode>();

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
			string usage = Path;

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

		public CommandArgument(string name)
		{
			Name = name;
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
	}

	public class CommandStringArgument : CommandArgument
	{
		public string[] PossibleValues;

		public CommandStringArgument(string name) : base(name)
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

	}

	public class CommandIntArgument : CommandArgument
	{
		public int? MinValue;
		public int? MaxValue;

		public CommandIntArgument(string name, int? minValue = null, int? maxValue = null) : base(name)
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

		public CommandFloatArgument(string name) : base(name)
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
		public CommandBoolArgument(string name) : base(name)
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
	}

	public class CommandRemainingArgument : CommandArgument
	{
		public CommandRemainingArgument(string name) : base(name)
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


	public class Commander
	{
		private ConsoleCommandGroup RootCommands = new ConsoleCommandGroup("", "", "Top level commands");

		public void AddCommandGroup(string groupPath, string helpText)
		{
			string[] path = groupPath.ToLower().Split(' ');
			ConsoleCommandGroup currGroup = RootCommands;

			string groupName = null;
			for (int c = 0; c < path.Length - 1; c++)
			{
				groupName = path[c];
				if (!currGroup.SubCommands.TryGetValue(groupName, out CommandNode node))
				{
					throw new Exception("Command subgroup not created " + groupName);
				}

				currGroup = node as ConsoleCommandGroup;
				if (currGroup == null)
				{
					throw new Exception("Command group conflicts with command name: " + groupName);
				}
			}

			groupName = path[path.Length - 1];
			if (currGroup.SubCommands.ContainsKey(groupName))
				throw new Exception("Group name conflict: " + groupName);

			ConsoleCommandGroup commandGroup = new ConsoleCommandGroup(groupName, groupPath, helpText);
			currGroup.SubCommands.Add(groupName, commandGroup);
		}

		public void AddCommand(string commandPath, Delegate action, string helpText, CommandArgument[] arguments)
		{
			ValidateArguments(arguments);

			string[] path = commandPath.ToLower().Split(' ');
			ConsoleCommandGroup currGroup = RootCommands;

			for (int c = 0; c < path.Length - 1; c++)
			{
				string groupName = path[c];
				if (!currGroup.SubCommands.TryGetValue(groupName, out CommandNode node))
				{
					throw new Exception("Command subgroup not created " + groupName);
				}

				currGroup = node as ConsoleCommandGroup;
				if (currGroup == null)
				{
					throw new Exception("Command group conflicts with command name: " + groupName);
				}
			}

			string commandName = path[path.Length - 1];
			if (currGroup.SubCommands.ContainsKey(commandName))
				throw new Exception("Command name conflict: " + commandName);

			ConsoleCommand command = new ConsoleCommand(commandName, commandPath, action, arguments, helpText);
			currGroup.SubCommands.Add(commandName, command);
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


		public void Process(string commandLine)
		{
			commandLine = commandLine.Trim();
			if (String.IsNullOrEmpty(commandLine))
				return;

			List<string> tokens = null;
			try
			{
				tokens = CommandTokenizer.Tokenize(commandLine);
			}
			catch (Exception e)
			{
				Cons.LogError("Error parsing command: " + e.Message);
				return;
			}

			RunCommand(tokens);
		}

		public CommandNode GetCommandInfo(string commandPath)
		{
			commandPath = commandPath.Trim();
			if (String.IsNullOrEmpty(commandPath))
				return RootCommands;

			string[] path = commandPath.ToLower().Split(' ');
			ConsoleCommandGroup currGroup = RootCommands;

			string groupName;
			for (int c = 0; c < path.Length - 1; c++)
			{
				groupName = path[c];
				currGroup.SubCommands.TryGetValue(groupName, out CommandNode node);
				currGroup = node as ConsoleCommandGroup;
				if (currGroup == null)
				{
					return null;
				}
			}

			groupName = path[path.Length - 1];
			currGroup.SubCommands.TryGetValue(groupName, out CommandNode command);
			return command;
		}

		private void RunCommand(List<string> argv)
		{
			ConsoleCommandGroup currGroup = RootCommands;

			int pos = 0;
			while (pos < argv.Count)
			{
				string pathPart = argv[pos++];

				if (!currGroup.SubCommands.TryGetValue(pathPart, out CommandNode node))
				{
					Cons.LogWarning("Unknown command: " + String.Join(" ", argv));
					return;
				}

				ConsoleCommand command = node as ConsoleCommand;
				if (command != null)
				{
					RunCommand(command, argv.GetRange(pos, argv.Count - pos));
					return;
				}

				currGroup = node as ConsoleCommandGroup;
			}

			// If the command is a subgroup name, do help for that group
			BuiltinCommands.Help(argv.ToArray());
		}

		private void RunCommand(ConsoleCommand command, List<string> argv)
		{
			if (command.Arguments != null)
			{
				object[] args = ParseArguments(command, argv);
				if (args == null)
					return;

				command.CommandAction.DynamicInvoke(args);
			}
			else
			{
				command.CommandAction.DynamicInvoke(null);
			}
		}

		private object[] ParseArguments(ConsoleCommand command, List<string> argv)
		{
			object[] args = new object[command.Arguments.Length];

			try
			{
				for (int a = 0; a < command.Arguments.Length; a++)
				{
					CommandArgument arg = command.Arguments[a];

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
			}
			catch (Exception e)
			{
				Cons.LogWarning("Argument error: " + e.Message);
				Cons.Log("Usage: " + command.GetUsageString());
				return null;
			}

			return args;
		}

		public List<string> GetPossibleCommands(string[] partialPath)
		{
			ConsoleCommandGroup currGroup = RootCommands;
			CommandNode node;
			for (int p = 0; p < partialPath.Length - 1; p++)
			{
				if (!currGroup.SubCommands.TryGetValue(partialPath[p], out node))
					return null;

				currGroup = node as ConsoleCommandGroup;
				if (currGroup == null)
					return null;
			}

			string partialCommand = partialPath[partialPath.Length - 1];
			if (currGroup.SubCommands.TryGetValue(partialCommand, out node))
			{
				// It wasn't partial at all
				return new List<string> { partialCommand };
			}

			List<string> matches = null;
			foreach (string key in currGroup.SubCommands.Keys)
			{
				if (key.StartsWith(partialCommand))
				{
					if (matches == null)
						matches = new List<string>();
					matches.Add(key);
				}
			}

			return matches;
		}
	}

}
