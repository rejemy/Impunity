using System;
using System.Threading.Tasks;

using UnityEngine;

namespace Dreamwing.Cons
{
	// The answer of a console command that finishes on a later frame — one waiting on a server
	// round trip. Get one from Cons.Defer() while the command runs, log through it, then Finish.
	// A captured run (Cons.RunCommandCapturingOutputAsync, behind `bv console`) waits for it, so
	// the lines reach whoever ran the command; typed into the panel, they just appear there.
	// Main thread only.
	public sealed class ConsoleReply
	{
		// Where lines go while a captured run waits; null = the panel
		internal Action<string, bool> Sink;
		internal readonly TaskCompletionSource<bool> Finished = new TaskCompletionSource<bool>();

		public bool IsFinished => Finished.Task.IsCompleted;

		internal ConsoleReply() { }

		public void Log(string message) => Write(LogType.Log, message);
		public void LogWarning(string message) => Write(LogType.Warning, message);
		public void LogError(string message) => Write(LogType.Error, message);

		// Ends the reply; later lines still show on the panel
		public void Finish()
		{
			Finished.TrySetResult(true);
		}

		// LogError then Finish: the usual way an asynchronous command fails
		public void Fail(string message)
		{
			LogError(message);
			Finish();
		}

		private void Write(LogType logType, string message)
		{
			Cons.WriteReplyLine(IsFinished ? null : Sink, logType, message);
		}
	}
}
