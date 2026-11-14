// Run Edit Mode tests with WebGL selected as the editor's active build target. A fake transport exercises
// the actual UNITY_WEBGL connection pump without browser interop, background threads, or timing assertions.
#if UNITY_WEBGL
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Impunity;
using Impunity.Connection;
using Impunity.GameState;
using Impunity.Networking;
using UltraLiteDB;

[DistributedEntity(1)]
public partial class WebGLPumpChannel : DistributedChannelBase
{
	public DistributedValue<int, Int32Serializer> Value;
}

public class WebGLSendPumpTests
{
	RecordingTransport Transport;
	RemoteGameConnection Connection;
	WebGLPumpChannel Channel;

	[SetUp]
	public void Connect()
	{
		Transport = new RecordingTransport();
		var format = new GameStateFormat(1, Array.Empty<GameStateCollection>(), new[] { typeof(WebGLPumpChannel) });
		Connection = new RemoteGameConnection(Transport, "pump", "", format, null, null);
		Connection.Connect(error => Assert.IsNull(error));
		for (int i = 0; i < 4 && !Connection.Connected; i++) Connection.Update();
		Assert.IsTrue(Connection.Connected);
		Connection.HandleCreateChannel(1, "pump", 1, false, 0, default, 0);
		Channel = (WebGLPumpChannel)Connection.EntityManager.CurrentSubscriptions[0];
	}

	[TearDown]
	public void Disconnect() => Connection?.Dispose();

	[TestCase(false)]
	[TestCase(true)]
	public void DirtyFields_AreSentInTheSameUpdate(bool guaranteed)
	{
		if (guaranteed) Channel.Value.Set(42);
		else Channel.Value.SetUnguaranteed(42);

		Connection.Update();

		Assert.AreEqual(1, Transport.Updates.Count, "the dirty sweep must not leave its update until the next frame");
		Assert.AreEqual(42, ReadValue(Transport.Updates[0]));
		Assert.AreEqual(guaranteed, Transport.Delivery[0]);
		Connection.Update();
		Assert.AreEqual(1, Transport.Updates.Count, "a second drain must not send the update twice");
	}

	[Test]
	public void AlreadyQueuedBytes_AreSentBeforeTheDirtySweepReusesItsBuffer()
	{
		Channel.Value.Set(11);
		var bytes = Connection.EntityManager.GetPropertyBytes(Channel, out bool guaranteed);
		Connection.UpdateEntity(1, bytes, guaranteed, 1, null);
		Channel.Value.Set(22);

		Connection.Update();

		Assert.AreEqual(2, Transport.Updates.Count);
		Assert.AreEqual(11, ReadValue(Transport.Updates[0]), "the next sweep must not overwrite an earlier queued payload");
		Assert.AreEqual(22, ReadValue(Transport.Updates[1]));
	}

	[Test]
	public void CallbackQueuedRequest_IsSentThisUpdate_ButItsCallbackWaitsForTheNext()
	{
		bool firstCalled = false, secondCalled = false;
		Connection.DoAction(new GetTimeAction((error, time) =>
		{
			firstCalled = true;
			Connection.DoAction(new GetTimeAction((nextError, nextTime) => secondCalled = true));
		}));
		int before = Transport.TimeRequests;

		Connection.Update();

		Assert.IsTrue(firstCalled);
		Assert.AreEqual(before + 2, Transport.TimeRequests);
		Assert.IsFalse(secondCalled, "even an immediate transport reply must wait for the next inbound pump");
		Connection.Update();
		Assert.IsTrue(secondCalled);
	}

	[Test]
	public void DisposeInCallback_DoesNotSendTheSweepsQueuedUpdateOrFlushAClosedTransport()
	{
		Channel.Value.Set(42);
		Connection.DoAction(new GetTimeAction((error, time) => Connection.Dispose()));

		Connection.Update();
		Connection.Update();

		Assert.IsTrue(Transport.Disposed);
		Assert.AreEqual(0, Transport.Updates.Count);
		Assert.AreEqual(0, Transport.CallsAfterDispose);
	}

	static int ReadValue(UpdateEntityAction update)
	{
		var bytes = update.UpdateBytes;
		using var reader = new BinaryReader(new MemoryStream(bytes.Array, bytes.Offset, bytes.Count));
		Assert.AreEqual(1, reader.ReadByte());
		int value = reader.ReadInt32();
		Assert.AreEqual(0, reader.ReadByte());
		return value;
	}

	sealed class RecordingTransport : IImpunityNetworkClient
	{
		public string ConnectionId => "pump";
		public ImpunityClientMessageHandler OnMessageRecieved { get; set; }
		public ImpunityClientMessageHandler OnUnguaranteedMessageRecieved { get; set; }
		public ImpunityCallback OnNetworkError { get; set; }
		public Action<int> OnDisconnectedByServer { get; set; }
		public bool SupportsUnguaranteed => true;
		public int MaxUnguaranteedMessageBytes => ImpunityConstants.MaxUnguaranteedMessageSize;
		public readonly List<UpdateEntityAction> Updates = new List<UpdateEntityAction>();
		public readonly List<bool> Delivery = new List<bool>();
		public int TimeRequests, CallsAfterDispose;
		public bool Disposed;
		public void Connect(ImpunityCallback onComplete) => onComplete(null);
		public void Disconnect() => Disposed = true;
		public void Dispose() => Disposed = true;
		public void Flush() { if (Disposed) CallsAfterDispose++; }
		public void SendGuaranteedMessage(ArraySegment<byte> bytes) => Send(bytes, true);
		public void SendUnguaranteedMessage(ArraySegment<byte> bytes) => Send(bytes, false);

		void Send(ArraySegment<byte> bytes, bool guaranteed)
		{
			if (Disposed) CallsAfterDispose++;
			// Preserve the wire bytes, since the connection reuses its encoder for the next send.
			byte[] copy = new byte[bytes.Count];
			Array.Copy(bytes.Array, bytes.Offset, copy, 0, bytes.Count);
			int offset = ImpunityNetworkingUtil.ReadMessageHeader(new ArraySegment<byte>(copy), out var header);
			ActionResult reply;
			switch ((ClientActionType)header.MessageType)
			{
				case ClientActionType.UPDATE_ENTITY:
					Updates.Add((UpdateEntityAction)ImpunityUtil.GetBsonMapper().DeserializeFromBytes(typeof(UpdateEntityAction), copy, offset));
					Delivery.Add(guaranteed);
					return;
				case ClientActionType.ESTABLISH_CONNECTION:
					reply = new ActionResult<EstablishConnectResult> { Result = new EstablishConnectResult { ConnectionId = "pump", ConnectionTimeoutMillis = 0 } };
					break;
				case ClientActionType.GET_TIME:
					TimeRequests++;
					reply = new ActionResult<long> { Result = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
					break;
				default:
					return;
			}
			var writer = new ByteWriter(new byte[4096]);
			OnMessageRecieved(ImpunityNetworkingUtil.WriteMessage(writer, header.MessageId, 0, (ushort)ServerActionType.CLIENT_REPLY, reply));
		}
	}
}
#endif
