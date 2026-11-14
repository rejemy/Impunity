using System;
using System.Collections.Generic;
using System.Linq;

using NUnit.Framework;

using Dreamwing.Cons;


namespace DevTools.Tests
{
	// Dispatch, tab completion, suggestions and help on a small command tree:
	//   help <command path...>, warp <area> [x] [y], wind, window, setmute <bool>,
	//   speed <fast/slow>, console clear / console close,
	//   inv (alone = list) / inv give <item> [count] / inv clear,
	//   say [text] (alone = say with no text) / say loud,
	//   a b c (three levels)
	public class CommanderTests
	{
		Commander Cmds;
		List<string> Invoked;
		RecordingOutput Output;

		class RecordingOutput : ICommandOutput
		{
			public readonly List<string> Lines = new List<string>();
			public readonly List<string> Warnings = new List<string>();
			public readonly List<(string Name, string Text)> Rows = new List<(string Name, string Text)>();

			public void Log(string message) => Lines.Add(message);
			public void LogWarning(string message) => Warnings.Add(message);
			public void LogRows(IList<(string Name, string Text)> rows) => Rows.AddRange(rows);
		}

		[SetUp]
		public void SetUp()
		{
			Cmds = new Commander();
			Output = new RecordingOutput();
			Cmds.Output = Output;
			Invoked = new List<string>();

			Cmds.AddCommand("help", (Action<string[]>)(args => Invoked.Add("help " + String.Join(" ", args))), "",
				new CommandArgument[] { new CommandPathArgument("command") });
			Cmds.AddCommand("warp", (Action<string, int, int>)((area, x, y) => Invoked.Add("warp " + area + " " + x + " " + y)), "",
				new CommandArgument[] { new CommandStringArgument("area"), new CommandIntArgument("x", -1), new CommandIntArgument("y", -1) });
			Cmds.AddCommand("wind", (Action)(() => Invoked.Add("wind")), "", null);
			Cmds.AddCommand("window", (Action)(() => Invoked.Add("window")), "", null);
			Cmds.AddCommand("setmute", (Action<bool>)(mute => Invoked.Add("setmute " + mute)), "",
				new CommandArgument[] { new CommandBoolArgument("mute") });
			Cmds.AddCommand("speed", (Action<string>)(speed => Invoked.Add("speed " + speed)), "",
				new CommandArgument[] { new CommandStringArgument("speed") { PossibleValues = new[] { "fast", "slow" } } });
			Cmds.AddCommandGroup("console", "");
			Cmds.AddCommand("console clear", (Action)(() => Invoked.Add("console clear")), "", null);
			Cmds.AddCommand("console close", (Action)(() => Invoked.Add("console close")), "", null);

			Cmds.AddCommandGroup("inv", "Player inventory");
			Cmds.AddCommand("inv", (Action)(() => Invoked.Add("inv")), "Lists the inventory. Empty slots skipped", null);
			Cmds.AddCommand("inv give", (Action<string, int>)((item, count) => Invoked.Add("inv give " + item + " " + count)),
				"Adds items to the inventory (merges stacks)",
				new CommandArgument[] { new CommandStringArgument("item"), new CommandIntArgument("count", 1) });
			Cmds.AddCommand("inv clear", (Action)(() => Invoked.Add("inv clear")), "Empties the inventory", null);

			Cmds.AddCommandGroup("say", "Talking");
			Cmds.AddCommand("say", (Action<string>)(text => Invoked.Add("say " + text)), "Says something",
				new CommandArgument[] { new CommandStringArgument("text", "") { PossibleValues = new[] { "", "hello", "help" } } });
			Cmds.AddCommand("say loud", (Action)(() => Invoked.Add("say loud")), "Says it loudly", null);

			Cmds.AddCommandGroup("a", "Level one");
			Cmds.AddCommandGroup("a b", "Level two");
			Cmds.AddCommand("a b c", (Action)(() => Invoked.Add("a b c")), "Level three", null);
		}

		[Test]
		public void Complete_UniqueCommandPrefix_CompletesWithSpace()
		{
			Assert.AreEqual("warp ", Cmds.Complete("wa").Line);
		}

		[Test]
		public void Complete_UppercasePrefix_CompletesToRegisteredName()
		{
			Assert.AreEqual("warp ", Cmds.Complete("WA").Line);
		}

		[Test]
		public void Complete_AmbiguousPrefix_ExtendsToCommonPrefix()
		{
			CommandCompletion completion = Cmds.Complete("wi");

			Assert.AreEqual("wind", completion.Line);
			Assert.IsNull(completion.Candidates);
		}

		[Test]
		public void Complete_NothingCommonToAdd_ListsCandidatesAndKeepsLine()
		{
			CommandCompletion completion = Cmds.Complete("w");

			Assert.AreEqual("w", completion.Line);
			CollectionAssert.AreEqual(new[] { "warp", "wind", "window" }, completion.Candidates);
		}

		[Test]
		public void Complete_ExactNameThatPrefixesAnother_ListsBoth()
		{
			// "wind" is complete, but "window" also matches: show both rather than
			// guessing, so the user can add a space or keep typing
			CommandCompletion completion = Cmds.Complete("wind");

			Assert.AreEqual("wind", completion.Line);
			CollectionAssert.AreEqual(new[] { "wind", "window" }, completion.Candidates);
		}

		[Test]
		public void Complete_EmptyLine_ListsAllTopLevelNames()
		{
			CommandCompletion completion = Cmds.Complete("");

			CollectionAssert.AreEqual(new[] { "a", "console", "help", "inv", "say", "setmute", "speed", "warp", "wind", "window" }, completion.Candidates);
		}

		[Test]
		public void Complete_GroupThenSpace_CompletesAmongSubcommands()
		{
			// Both subcommands start "cl": extend first, list on the next Tab
			Assert.AreEqual("console cl", Cmds.Complete("console ").Line);

			CommandCompletion completion = Cmds.Complete("console cl");
			Assert.AreEqual("console cl", completion.Line);
			CollectionAssert.AreEqual(new[] { "clear", "close" }, completion.Candidates);
		}

		[Test]
		public void Complete_Subcommand_CompletesWithinGroup()
		{
			Assert.AreEqual("console cl", Cmds.Complete("console c").Line);
			Assert.AreEqual("console clear ", Cmds.Complete("console cle").Line);
		}

		[Test]
		public void Complete_BoolArgument_CompletesTrueFalse()
		{
			Assert.AreEqual("setmute true ", Cmds.Complete("setmute t").Line);
			CollectionAssert.AreEqual(new[] { "false", "true" }, Cmds.Complete("setmute ").Candidates);
		}

		[Test]
		public void Complete_ArgumentWithPossibleValues_CompletesThem()
		{
			Assert.AreEqual("speed slow ", Cmds.Complete("speed s").Line);
		}

		[Test]
		public void Complete_FreeFormArgument_LeavesLineAlone()
		{
			CommandCompletion completion = Cmds.Complete("warp sta");

			Assert.AreEqual("warp sta", completion.Line);
			Assert.IsNull(completion.Candidates);
		}

		[Test]
		public void Complete_PastLastArgument_LeavesLineAlone()
		{
			Assert.AreEqual("setmute true f", Cmds.Complete("setmute true f").Line);
		}

		[Test]
		public void Complete_HelpArgument_CompletesCommandPaths()
		{
			Assert.AreEqual("help warp ", Cmds.Complete("help wa").Line);
			Assert.AreEqual("help console close ", Cmds.Complete("help console clo").Line);
		}

		[Test]
		public void Complete_KeepsEarlierWordsAsTyped()
		{
			// Only the last word is touched: earlier arguments keep their case
			Assert.AreEqual("help CONSOLE close ", Cmds.Complete("help CONSOLE clo").Line);
		}

		[Test]
		public void Complete_UnknownCommand_LeavesLineAlone()
		{
			Assert.AreEqual("nope x", Cmds.Complete("nope x").Line);
		}

		[Test]
		public void Complete_InsideUnclosedQuote_LeavesLineAlone()
		{
			Assert.AreEqual("warp \"big ar", Cmds.Complete("warp \"big ar").Line);
		}

		[Test]
		public void Process_UppercaseCommandName_RunsCommand()
		{
			Cmds.Process("CONSOLE Clear");

			CollectionAssert.AreEqual(new[] { "console clear" }, Invoked);
		}

		[Test]
		public void Process_OptionalArgumentsOmitted_UsesDefaults()
		{
			Cmds.Process("warp Farm");

			CollectionAssert.AreEqual(new[] { "warp Farm -1 -1" }, Invoked);
		}

		[Test]
		public void Process_TooManyArguments_DoesNotRun()
		{
			Cmds.Process("warp farm 1 2 junk");
			Cmds.Process("wind extra");

			CollectionAssert.IsEmpty(Invoked);
		}

		[Test]
		public void Process_RemainingArgument_TakesAllWords()
		{
			Cmds.Process("help console close");

			CollectionAssert.AreEqual(new[] { "help console close" }, Invoked);
		}

		// ---- the verdict (dev tooling exits non-zero on false) -------------------

		[Test]
		public void Process_CommandRuns_ReturnsTrue()
		{
			Assert.IsTrue(Cmds.Process("warp farm 1 2"));
			Assert.IsTrue(Cmds.Process("say hello"));
			Assert.IsTrue(Cmds.Process("   "));
		}

		[Test]
		public void Process_GroupWithoutDefault_ShowsHelpAndReturnsTrue()
		{
			Assert.IsTrue(Cmds.Process("a"));
		}

		[Test]
		public void Process_UnknownCommand_ReturnsFalse()
		{
			Assert.IsFalse(Cmds.Process("nosuch"));
			Assert.IsFalse(Cmds.Process("inv gve axe"));
		}

		[Test]
		public void Process_BadArguments_ReturnsFalse()
		{
			Assert.IsFalse(Cmds.Process("warp farm x"));
			Assert.IsFalse(Cmds.Process("wind extra"));
			Assert.IsFalse(Cmds.Process("speed medium"));
		}

		[Test]
		public void Process_UnclosedQuote_ReturnsFalse()
		{
			Assert.IsFalse(Cmds.Process("warp \"big area"));
		}

		[Test]
		public void Process_CommandThrows_ReturnsFalse()
		{
			Cmds.AddCommand("boom", (Action)(() => throw new InvalidOperationException("boom")), "", null);
			UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Exception, "InvalidOperationException: boom");

			Assert.IsFalse(Cmds.Process("boom"));
		}

		// ---- groups with a default command -------------------------------------

		[Test]
		public void Process_GroupWithDefault_RunsDefault()
		{
			Cmds.Process("inv");

			CollectionAssert.AreEqual(new[] { "inv" }, Invoked);
		}

		[Test]
		public void Process_GroupWithDefault_ChildNameWins()
		{
			Cmds.Process("inv give axe 2");
			Cmds.Process("say loud");

			CollectionAssert.AreEqual(new[] { "inv give axe 2", "say loud" }, Invoked);
		}

		[Test]
		public void Process_GroupDefaultWithArgs_PassesRemainingTokens()
		{
			Cmds.Process("say hello");

			CollectionAssert.AreEqual(new[] { "say hello" }, Invoked);
		}

		[Test]
		public void Process_DefaultWithoutArgs_UnknownWordIsUnknownSubcommand()
		{
			Cmds.Process("inv gve axe");

			CollectionAssert.IsEmpty(Invoked);
			Assert.AreEqual("Unknown command: inv gve", Output.Warnings.Single());
			StringAssert.Contains("inv give", Output.Lines.Single());
		}

		[Test]
		public void Process_ThreeLevelPath_Runs()
		{
			Cmds.Process("A B C");

			CollectionAssert.AreEqual(new[] { "a b c" }, Invoked);
		}

		[Test]
		public void Process_BareGroupWithoutDefault_ShowsHelp()
		{
			Cmds.Process("console");

			CollectionAssert.IsEmpty(Invoked);
			CollectionAssert.AreEqual(new[] { "clear", "close" }, Output.Rows.Select(r => r.Name));
		}

		[Test]
		public void Process_IntermediateGroupWithoutDefault_ShowsItsHelp()
		{
			Cmds.Process("a b");

			CollectionAssert.AreEqual(new[] { "c" }, Output.Rows.Select(r => r.Name));
		}

		[Test]
		public void AddCommand_DefaultBeforeGroup_Throws()
		{
			Cmds.AddCommand("solo", (Action)(() => { }), "", null);

			Assert.Throws<Exception>(() => Cmds.AddCommandGroup("solo", ""));
		}

		[Test]
		public void AddCommand_SecondDefault_Throws()
		{
			Assert.Throws<Exception>(() => Cmds.AddCommand("inv", (Action)(() => { }), "", null));
		}

		[Test]
		public void AddCommand_DefaultArgValuesCollideWithChild_Throws()
		{
			// "help" is one of say's text values, so a "say help" subcommand would
			// make that value unreachable
			Assert.Throws<Exception>(() => Cmds.AddCommand("say help", (Action)(() => { }), "", null));
		}

		[Test]
		public void AddCommand_MissingParentGroup_Throws()
		{
			Assert.Throws<Exception>(() => Cmds.AddCommand("nope sub", (Action)(() => { }), "", null));
		}

		// ---- suggestions --------------------------------------------------------

		[Test]
		public void Suggest_TypoInGroup_SuggestsSibling()
		{
			CollectionAssert.AreEqual(new[] { "inv give" }, Cmds.Suggest(new List<string> { "inv", "gve" }));
		}

		[Test]
		public void Suggest_LeafNameAtRoot_SuggestsFullPath()
		{
			CollectionAssert.AreEqual(new[] { "inv give" }, Cmds.Suggest(new List<string> { "give", "axe" }));
		}

		[Test]
		public void Suggest_ShortWordTwoEditsAway_NotSuggested()
		{
			// "wave" is two edits from "warp": too loose a match for a four-letter word
			CollectionAssert.IsEmpty(Cmds.Suggest(new List<string> { "wave" }));
		}

		[Test]
		public void Suggest_ExactNameElsewhere_ComesBeforeNearMiss()
		{
			Cmds.AddCommand("lout", (Action)(() => { }), "", null);

			CollectionAssert.AreEqual(new[] { "say loud", "lout" }, Cmds.Suggest(new List<string> { "loud" }));
		}

		[Test]
		public void Suggest_PrefixOfGroupName_SuggestsGroup()
		{
			CollectionAssert.Contains(Cmds.Suggest(new List<string> { "consol" }), "console");
		}

		[Test]
		public void Suggest_NothingClose_ReturnsEmpty()
		{
			CollectionAssert.IsEmpty(Cmds.Suggest(new List<string> { "xyzzy" }));
		}

		[Test]
		public void Suggest_ResolvedLine_ReturnsEmpty()
		{
			CollectionAssert.IsEmpty(Cmds.Suggest(new List<string> { "inv", "give", "axe" }));
		}

		[Test]
		public void Process_UnknownTopLevelCommand_SuggestsGroupedCommand()
		{
			Cmds.Process("give axe");

			CollectionAssert.IsEmpty(Invoked);
			Assert.AreEqual("Did you mean: inv give?", Output.Lines.Single());
		}

		// ---- completion ---------------------------------------------------------

		[Test]
		public void Complete_GroupWithDefault_OffersChildrenAndDefaultArgValues()
		{
			CommandCompletion completion = Cmds.Complete("say ");

			CollectionAssert.AreEqual(new[] { "", "hello", "help", "loud" }, completion.Candidates);
			Assert.AreEqual("say hel", Cmds.Complete("say h").Line);
		}

		[Test]
		public void Complete_GroupWithDefault_CompletesDefaultArgsPastFirst()
		{
			// "hello" isn't a subcommand, so the words are the default's arguments
			Assert.AreEqual("say hello x", Cmds.Complete("say hello x").Line);
		}

		[Test]
		public void Complete_ThreeLevelPath_Completes()
		{
			Assert.AreEqual("a b ", Cmds.Complete("a ").Line);
			Assert.AreEqual("a b c ", Cmds.Complete("a b ").Line);
		}

		[Test]
		public void Complete_HelpArgument_CompletesThroughGroupsAndOptions()
		{
			Assert.AreEqual("help inv give ", Cmds.Complete("help inv g").Line);
			Assert.AreEqual("help --all inv give ", Cmds.Complete("help --all inv g").Line);
		}

		[Test]
		public void Complete_StringArgumentCompleter_Completes()
		{
			Cmds.AddCommand("pick", (Action<string>)(item => { }), "",
				new CommandArgument[] { new CommandStringArgument("item") { Completer = () => new[] { "apple", "axe" } } });

			Assert.AreEqual("pick apple ", Cmds.Complete("pick ap").Line);
			CollectionAssert.AreEqual(new[] { "apple", "axe" }, Cmds.Complete("pick a").Candidates);
		}

		[Test]
		public void Complete_ThrowingCompleter_OffersNothing()
		{
			Cmds.AddCommand("pick", (Action<string>)(item => { }), "",
				new CommandArgument[] { new CommandStringArgument("item") { Completer = () => throw new InvalidOperationException() } });

			Assert.AreEqual("pick a", Cmds.Complete("pick a").Line);
		}

		[Test]
		public void Process_CompleterValuesAreNotEnforced()
		{
			Cmds.AddCommand("pick", (Action<string>)(item => Invoked.Add("pick " + item)), "",
				new CommandArgument[] { new CommandStringArgument("item") { Completer = () => new[] { "apple" } } });

			Cmds.Process("pick pear");

			CollectionAssert.AreEqual(new[] { "pick pear" }, Invoked);
		}

		// ---- help ---------------------------------------------------------------

		[Test]
		public void DescribeGroup_SplitsGroupsAndCommands()
		{
			var (groups, commands) = Cmds.DescribeGroup((ConsoleCommandGroup)Cmds.GetCommandInfo(""));

			CollectionAssert.AreEqual(new[] { "a", "console", "inv", "say" }, groups.Select(r => r.Name));
			CollectionAssert.AreEqual(new[] { "help", "setmute", "speed", "warp", "wind", "window" }, commands.Select(r => r.Name));
		}

		[Test]
		public void Help_GroupWithDefault_ShowsDefaultAndSummaries()
		{
			Cmds.Help(new[] { "inv" });

			CollectionAssert.Contains(Output.Lines, "Alone: inv  Lists the inventory");
			CollectionAssert.AreEqual(new[] { ("clear", "Empties the inventory"), ("give", "Adds items to the inventory (merges stacks)") }, Output.Rows);
		}

		[Test]
		public void Help_All_ListsTreeIndented()
		{
			Cmds.Help(new[] { "--all", "a" });

			CollectionAssert.AreEqual(new[] { "a … — Level one", "  b … — Level two", "    c — Level three" }, Output.Lines);
		}

		[Test]
		public void Help_UnknownPath_WarnsAndSuggests()
		{
			Cmds.Help(new[] { "give" });

			Assert.AreEqual("Command not found: give", Output.Warnings.Single());
			Assert.AreEqual("Did you mean: inv give?", Output.Lines.Single());
		}

		[Test]
		public void Find_MatchesHelpTextAndDefaultHelp()
		{
			CollectionAssert.AreEqual(new[] { "inv", "inv clear", "inv give" }, Cmds.Find("INVENTORY").Select(n => n.Path));
			CollectionAssert.AreEqual(new[] { "inv" }, Cmds.Find("slots skipped").Select(n => n.Path));
		}

		[Test]
		public void SummaryOf_TakesFirstSentence()
		{
			Assert.AreEqual("Sets the scale (1 = normal)", Commander.SummaryOf("Sets the scale (1 = normal). 0 = frozen"));
		}

		[Test]
		public void SummaryOf_IgnoresPeriodsInsideParentheses()
		{
			Assert.AreEqual("Plays a state (e.g. Arms) locally", Commander.SummaryOf("Plays a state (e.g. Arms) locally. More"));
		}

		[Test]
		public void SummaryOf_SingleSentence_DropsTrailingPeriod()
		{
			Assert.AreEqual("Quits", Commander.SummaryOf("Quits."));
		}
	}
}
