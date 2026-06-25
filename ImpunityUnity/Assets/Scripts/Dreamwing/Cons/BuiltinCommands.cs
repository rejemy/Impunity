using System;
using System.Collections.Generic;

using UnityEngine;

namespace Dreamwing.Cons
{

	public static class BuiltinCommands
	{
		public static void Init()
		{
			Cons.AddCommand<string[]>(
				"help",
				BuiltinCommands.Help,
				"Prints command help text and lists commands",
				new CommandRemainingArgument("command")

			);

			Cons.AddCommand(
				"quit",
				BuiltinCommands.Quit,
				"Quits program"
			);

			Cons.AddCommandGroup("console", "Console commands");
			Cons.AddCommand(
				"console clear", ConsoleClear, "Clears console output"
			);
			Cons.AddCommand(
				"console clearcommands", ConsoleClearHistory, "Clears console command history"
			);
			Cons.AddCommand(
				"console close", ConsoleClose, "Closes console"
			);

			Cons.AddCommandGroup("system", "Commands related to the dotnet system");
			Cons.AddCommand(
				"system gc", SystemGC, "Runs system garbage collection immediately"
			);
			Cons.AddCommand(
				"system info", SystemPrintInfo, "Prints info about the system"
			);
		}

		public static void Help(string[] args)
		{
			string commandPath = String.Join(" ", args);
			CommandNode node = Cons.GetCommandInfo(commandPath);
			if (node == null)
			{
				Cons.LogWarning("Command not found: " + commandPath);
				return;
			}

			ConsoleCommand cmd = node as ConsoleCommand;
			if (cmd != null)
			{
				Cons.Log(node.HelpText);
				Cons.Log("Usage: " + cmd.GetUsageString());
			}

			ConsoleCommandGroup grp = node as ConsoleCommandGroup;
			if (grp != null)
			{
				Cons.Log(node.HelpText + ":");
				List<CommandNode> subcommands = new List<CommandNode>(grp.SubCommands.Values);
				subcommands.Sort((n1, n2) =>
				{
					return String.Compare(n1.Path, n2.Path);
				});
				foreach (CommandNode sub in subcommands)
				{
					Cons.Log("  " + sub.Path);
				}
			}
		}

		public static void Quit()
		{
#if UNITY_EDITOR

			UnityEditor.EditorApplication.isPlaying = false;
#else
			Application.Quit();
#endif
		}

		public static void ConsoleClear()
		{
			Cons.Clear();
		}

		public static void ConsoleClearHistory()
		{
			Cons.ClearHistory();
		}

		public static void ConsoleClose()
		{
			Cons.Close();
		}

		public static void SystemGC()
		{
			System.GC.Collect();
			Cons.Log("Garbage collected.");
		}

		public static void SystemPrintInfo()
		{
			long megsUsed = System.GC.GetTotalMemory(true) / 1048576L;
			Cons.Log(
				"Device: " + SystemInfo.deviceType + " " + SystemInfo.deviceModel + "\n" +
				"Processor: " + SystemInfo.processorType + " x" + SystemInfo.processorCount + " @" + SystemInfo.processorFrequency + "\n" +
				"GPU: " + SystemInfo.graphicsDeviceVendor + " " + SystemInfo.graphicsDeviceType + " " + SystemInfo.graphicsDeviceName + " " + SystemInfo.graphicsMemorySize + "M\n" +
				"Memory: " + megsUsed + "M / " + SystemInfo.systemMemorySize + "M\n" +
				"OS: " + SystemInfo.operatingSystemFamily + " " + SystemInfo.operatingSystem + " " + Application.systemLanguage + "\n" +
				"Build: " + Application.version + " " + Application.buildGUID + " Unity: " + Application.unityVersion
				);

		}

	}
}
