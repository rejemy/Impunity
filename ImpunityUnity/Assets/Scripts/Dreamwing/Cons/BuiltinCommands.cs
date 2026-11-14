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
				"Lists a group's commands or shows a command's usage. --all lists everything under the path, one line each; --find <text> searches names and help text",
				new CommandPathArgument("command")

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
				"console clear-history", ConsoleClearHistory, "Clears console command history"
			);
			Cons.AddCommand(
				"console close", ConsoleClose, "Closes console"
			);

			Cons.AddCommandGroup("system", "Runtime diagnostics: memory, device");
			Cons.AddCommand(
				"system gc", SystemGC, "Runs system garbage collection immediately"
			);
			Cons.AddCommand(
				"system info", SystemPrintInfo, "Prints info about the system"
			);
		}

		public static void Help(string[] args)
		{
			Cons.Help(args);
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
