// Unit coverage for DistributedStack: every mutation (Clear/Replace/Push/Pop/SetTop), the read-only
// surface, callbacks, and the wire format against ServerDistributedStack (delta apply, raw-byte relay,
// full-state resend, and SkipFrom stream alignment). Replication through a real server is covered by
// IntegrationTests.DistributedStack_Replication.
#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

using Impunity.Connection;
using Impunity.GameState;

namespace Impunity.Tests
{
	[DistributedEntity(StackTestEntity.TYPE_ID)]
	public partial class StackTestEntity : DistributedObjectBase
	{
		public const int TYPE_ID = 60;

		[Distributed(1)]
		public DistributedStack<string, StringSerializer> History;

		[Distributed(2)]
		public DistributedStack<int, Int32Serializer> Numbers;
	}

	public class DistributedStackTests
	{
		/// <summary>Connection-less entity — mutations apply to the local value immediately (the same
		/// client-authoritative path BsonSerializationTests uses), while still queueing pending changes
		/// for WriteChangesTo.</summary>
		static StackTestEntity MakeEntity()
		{
			return new StackTestEntity { IsClientAuthoritative = true };
		}

		// ───────── Local mutation + read-only surface ─────────

		[Test, Category("Stack")]
		public void StartsEmpty_NoInitRequired()
		{
			var e = MakeEntity();

			Assert.AreEqual(0, e.History.Count);
			Assert.IsFalse(e.History.TryPeek(out _));
			Assert.Throws<InvalidOperationException>(() => e.History.Peek());
			Assert.IsEmpty(e.History.ToList());
		}

		[Test, Category("Stack")]
		public void Push_AddsToTop_AndFiresOnPushed()
		{
			var e = MakeEntity();
			var pushed = new List<string>();
			e.History.OnPushed += v => pushed.Add(v);

			e.History.Push("a");
			e.History.Push("b");

			Assert.AreEqual(2, e.History.Count);
			Assert.AreEqual("b", e.History.Peek());
			Assert.IsTrue(e.History.TryPeek(out var top));
			Assert.AreEqual("b", top);
			Assert.AreEqual(new[] { "b", "a" }, e.History.ToArray(), "enumeration is top to bottom");
			Assert.AreEqual(new[] { "a", "b" }, pushed);
		}

		[Test, Category("Stack")]
		public void Pop_RemovesTop_AndFiresOnPopped()
		{
			var e = MakeEntity();
			var popped = new List<string>();
			e.History.OnPopped += v => popped.Add(v);

			e.History.Push("a");
			e.History.Push("b");
			e.History.Pop();

			Assert.AreEqual(1, e.History.Count);
			Assert.AreEqual("a", e.History.Peek());
			Assert.AreEqual(new[] { "b" }, popped);
		}

		[Test, Category("Stack")]
		public void Pop_OnEmptyStack_IsSafeNoOp()
		{
			var e = MakeEntity();
			bool anyPop = false;
			e.History.OnPopped += _ => anyPop = true;

			Assert.DoesNotThrow(() => e.History.Pop());
			Assert.AreEqual(0, e.History.Count);
			Assert.IsFalse(anyPop);
		}

		[Test, Category("Stack")]
		public void SetTop_ReplacesTop_AndFiresOnTopChanged()
		{
			var e = MakeEntity();
			string oldTop = null, newTop = null;
			e.History.OnTopChanged += (o, n) => { oldTop = o; newTop = n; };

			e.History.Push("a");
			e.History.Push("b");
			e.History.SetTop("z");

			Assert.AreEqual(2, e.History.Count, "SetTop must not change the stack size");
			Assert.AreEqual("z", e.History.Peek());
			Assert.AreEqual("b", oldTop);
			Assert.AreEqual("z", newTop);
		}

		[Test, Category("Stack")]
		public void SetTop_OnEmptyStack_Pushes()
		{
			var e = MakeEntity();
			var pushed = new List<string>();
			bool topChanged = false;
			e.History.OnPushed += v => pushed.Add(v);
			e.History.OnTopChanged += (o, n) => topChanged = true;

			e.History.SetTop("only");

			Assert.AreEqual(1, e.History.Count);
			Assert.AreEqual("only", e.History.Peek());
			Assert.AreEqual(new[] { "only" }, pushed, "SetTop on an empty stack reports a push");
			Assert.IsFalse(topChanged);
		}

		[Test, Category("Stack")]
		public void Replace_SetsContents_LastValueBecomesTop()
		{
			var e = MakeEntity();
			List<string> replacedOld = null, replacedNew = null;
			e.History.OnReplaced += (o, n) => { replacedOld = o; replacedNew = n; };

			e.History.Push("stale");
			e.History.Replace(new[] { "bottom", "middle", "top" });

			Assert.AreEqual(3, e.History.Count);
			Assert.AreEqual("top", e.History.Peek());
			Assert.AreEqual(new[] { "top", "middle", "bottom" }, e.History.ToArray());
			Assert.AreEqual(new[] { "stale" }, replacedOld);
			Assert.AreEqual(new[] { "bottom", "middle", "top" }, replacedNew, "backing list is bottom to top");
		}

		[Test, Category("Stack")]
		public void Clear_ResetsToEmpty_AndFiresOnReplaced()
		{
			var e = MakeEntity();
			e.History.Push("a");
			e.History.Push("b");

			List<string> replacedNew = null;
			e.History.OnReplaced += (o, n) => replacedNew = n;

			e.History.Clear();

			Assert.AreEqual(0, e.History.Count);
			Assert.IsFalse(e.History.TryPeek(out _));
			Assert.IsNotNull(replacedNew);
			Assert.IsEmpty(replacedNew);
		}

		[Test, Category("Stack")]
		public void MutationsAfterReplace_ApplyToPendingReplacement()
		{
			var e = MakeEntity();

			e.History.Replace(new[] { "a", "b" });
			e.History.Push("c");
			e.History.SetTop("c2");
			e.History.Pop();
			e.History.Pop();

			Assert.AreEqual(new[] { "a" }, e.History.ToArray());
		}

		// ───────── Wire format vs ServerDistributedStack ─────────

		static ArraySegment<byte> WriteChanges(Action<BinaryWriter> write)
		{
			var stream = new MemoryStream();
			var writer = new BinaryWriter(stream);
			write(writer);
			writer.Flush();
			return new ArraySegment<byte>(stream.ToArray());
		}

		static BinaryReader ReaderFor(ArraySegment<byte> bytes)
		{
			return new BinaryReader(new MemoryStream(bytes.Array, bytes.Offset, bytes.Count));
		}

		[Test, Category("StackWire")]
		public void Wire_DeltaOps_ApplyOnServer_AndRelayToOtherClient()
		{
			// Client A mutates and flushes a delta block
			var a = MakeEntity();
			a.History.Push("x");
			a.History.Push("y");
			a.History.SetTop("y2");
			a.History.Pop();
			a.History.Push("z");

			var deltaBytes = WriteChanges(w => a.History.WriteChangesTo(w));

			// Server applies the deltas (a fresh stack — deltas may arrive before any full set)
			var server = new ServerDistributedStack<DString>();
			server.ReadFrom(ReaderFor(deltaBytes));

			// A late subscriber gets the server's full state
			var lateJoiner = MakeEntity();
			var fullBytes = WriteChanges(w => server.WriteTo(w));
			lateJoiner.History.ReadInitialFrom(ReaderFor(fullBytes));

			Assert.AreEqual(a.History.ToArray(), lateJoiner.History.ToArray());
			Assert.AreEqual(new[] { "z", "x" }, lateJoiner.History.ToArray());

			// An existing subscriber gets the original delta bytes relayed and fires per-op callbacks
			var b = MakeEntity();
			var pushed = new List<string>();
			var popped = new List<string>();
			var topChanges = new List<string>();
			b.History.OnPushed += v => pushed.Add(v);
			b.History.OnPopped += v => popped.Add(v);
			b.History.OnTopChanged += (o, n) => topChanges.Add(o + ">" + n);

			b.History.ReadChangesFrom(ReaderFor(deltaBytes));

			Assert.AreEqual(new[] { "z", "x" }, b.History.ToArray());
			Assert.AreEqual(new[] { "x", "y", "z" }, pushed);
			Assert.AreEqual(new[] { "y2" }, popped);
			Assert.AreEqual(new[] { "y>y2" }, topChanges);
		}

		[Test, Category("StackWire")]
		public void Wire_Replace_SendsFullSet()
		{
			var a = MakeEntity();
			a.History.Push("stale");
			var _ = WriteChanges(w => a.History.WriteChangesTo(w)); // flush the push delta

			a.History.Replace(new[] { "n1", "n2" });
			var setBytes = WriteChanges(w => a.History.WriteChangesTo(w));

			// A subscriber with diverged state converges on the full set and reports the replacement
			var b = MakeEntity();
			b.History.Push("junk");
			List<string> replacedNew = null;
			b.History.OnReplaced += (o, n) => replacedNew = n;

			b.History.ReadChangesFrom(ReaderFor(setBytes));

			Assert.AreEqual(new[] { "n2", "n1" }, b.History.ToArray());
			Assert.AreEqual(new[] { "n1", "n2" }, replacedNew);

			var server = new ServerDistributedStack<DString>();
			server.ReadFrom(ReaderFor(setBytes));
			var echoed = MakeEntity();
			echoed.History.ReadInitialFrom(ReaderFor(WriteChanges(w => server.WriteTo(w))));
			Assert.AreEqual(new[] { "n2", "n1" }, echoed.History.ToArray());
		}

		[Test, Category("StackWire")]
		public void Wire_PopOnEmpty_IsNoOpOnServerAndClient()
		{
			var a = MakeEntity();
			a.History.Pop(); // queues a pop even though the local stack is empty (another client might have pushed)

			var deltaBytes = WriteChanges(w => a.History.WriteChangesTo(w));

			var server = new ServerDistributedStack<DString>();
			Assert.DoesNotThrow(() => server.ReadFrom(ReaderFor(deltaBytes)));

			var b = MakeEntity();
			Assert.DoesNotThrow(() => b.History.ReadChangesFrom(ReaderFor(deltaBytes)));
			Assert.AreEqual(0, b.History.Count);
		}

		[Test, Category("StackWire")]
		public void Wire_UntouchedField_WritesNone()
		{
			var a = MakeEntity();
			var noneBytes = WriteChanges(w => a.History.WriteChangesTo(w));
			Assert.AreEqual(1, noneBytes.Count, "an untouched stack serializes as a single None byte");

			var server = new ServerDistributedStack<DString>();
			server.ReadFrom(ReaderFor(noneBytes));

			var b = MakeEntity();
			b.History.ReadInitialFrom(ReaderFor(WriteChanges(w => server.WriteTo(w))));
			Assert.AreEqual(0, b.History.Count);
		}

		[Test, Category("StackWire")]
		public void SkipFrom_AdvancesPastDeltaAndSetBlocks_LeavingStreamAligned()
		{
			const byte sentinel = 0xEE;

			// Delta block (push/pop/settop use both int payloads and no payloads)
			var a = MakeEntity();
			a.Numbers.Push(10);
			a.Numbers.Pop();
			a.Numbers.SetTop(20);
			var deltaBytes = WriteChanges(w => { a.Numbers.WriteChangesTo(w); w.Write(sentinel); });

			var clientSkipper = MakeEntity();
			var reader = ReaderFor(deltaBytes);
			clientSkipper.Numbers.SkipFrom(reader);
			Assert.AreEqual(sentinel, reader.ReadByte(), "client SkipFrom must consume exactly the delta block");
			Assert.AreEqual(0, clientSkipper.Numbers.Count, "SkipFrom must not apply changes");

			var serverSkipper = new ServerDistributedStack<DInt32>();
			reader = ReaderFor(deltaBytes);
			serverSkipper.SkipFrom(reader);
			Assert.AreEqual(sentinel, reader.ReadByte(), "server SkipFrom must consume exactly the delta block");

			// Full-set block
			a.Numbers.Replace(new[] { 1, 2, 3 });
			var setBytes = WriteChanges(w => { a.Numbers.WriteChangesTo(w); w.Write(sentinel); });

			reader = ReaderFor(setBytes);
			clientSkipper.Numbers.SkipFrom(reader);
			Assert.AreEqual(sentinel, reader.ReadByte(), "client SkipFrom must consume exactly the set block");

			reader = ReaderFor(setBytes);
			serverSkipper.SkipFrom(reader);
			Assert.AreEqual(sentinel, reader.ReadByte(), "server SkipFrom must consume exactly the set block");
		}
	}
}
