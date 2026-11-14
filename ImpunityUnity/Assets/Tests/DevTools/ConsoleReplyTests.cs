using System.Threading.Tasks;

using NUnit.Framework;

using Dreamwing.Cons;

namespace DevTools.Tests
{
	// Cons.Defer + RunCapturingOutputAsync: a command that answers on a later frame still gets
	// its lines into the captured output (what `dev console` / `tools/ucmd game` return), its
	// errors fail the run, and a reply that never finishes times out instead of hanging.
	public class ConsoleReplyTests
	{
		[Test]
		public async Task DeferredReply_LinesJoinTheCapture_InOrder()
		{
			ConsoleReply reply = null;
			Task<(string output, bool failed)> run = Cons.RunCapturingOutputAsync(() =>
			{
				reply = Cons.Defer();
				reply.Log("asked the server");
				return true;
			}, 5f);

			Assert.IsFalse(run.IsCompleted, "The run finished before its reply did");
			await Task.Yield();
			reply.Log("the answer");
			reply.Finish();

			var (output, failed) = await run;
			Assert.AreEqual("asked the server\nthe answer", output);
			Assert.IsFalse(failed);
		}

		[Test]
		public async Task DeferredReply_Error_FailsTheRun()
		{
			var (output, failed) = await Cons.RunCapturingOutputAsync(() =>
			{
				ConsoleReply reply = Cons.Defer();
				Task.Yield().GetAwaiter().OnCompleted(() => reply.Fail("net sync: no server"));
				return true;
			}, 5f);

			StringAssert.Contains("net sync: no server", output);
			Assert.IsTrue(failed);
		}

		[Test]
		public async Task DeferredReply_NeverFinished_TimesOutAndLetsGo()
		{
			ConsoleReply reply = null;
			var (output, failed) = await Cons.RunCapturingOutputAsync(() =>
			{
				reply = Cons.Defer();
				return true;
			}, 0.05f);

			StringAssert.Contains("timed out", output);
			Assert.IsTrue(failed);
			Assert.IsFalse(reply.IsFinished);
		}

		[Test]
		public async Task NoDeferredReply_CompletesAtOnce()
		{
			var (output, failed) = await Cons.RunCapturingOutputAsync(() => false, 5f);
			Assert.AreEqual("", output);
			Assert.IsTrue(failed, "A line that didn't run is a failure");
		}

		[Test]
		public void DeferOutsideACapture_FinishesWithoutAWaiter()
		{
			ConsoleReply reply = Cons.Defer();
			reply.Finish();
			Assert.IsTrue(reply.IsFinished);
		}
	}
}
