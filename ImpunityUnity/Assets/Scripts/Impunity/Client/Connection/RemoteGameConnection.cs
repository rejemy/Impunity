using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Net;

using UltraLiteDB;

using Impunity.GameState;
using Impunity.Networking;

namespace Impunity.Connection
{

	public class RemoteGameConnection : BaseGameConnection
	{
		BlockingCollection<GameStateActionBase> PendingSend;
		ConcurrentQueue<GameStateActionBase> AwaitingReceive;
		ConcurrentQueue<GameStateActionBase> CompletedActions;

		public ImpunityCallback OnNetworkError { get; set; }

		IImpunityClient NetworkClient;
		Thread NetworkWriterThread;
		bool Running;

		byte[] SendBuffer;

		public RemoteGameConnection(IImpunityClient networkClient)
		{
			PendingSend = new BlockingCollection<GameStateActionBase>();
			AwaitingReceive = new ConcurrentQueue<GameStateActionBase>();
			CompletedActions = new ConcurrentQueue<GameStateActionBase>();

			NetworkClient = networkClient;
			NetworkClient.OnNetworkError = OnNetworkErrorReceived;
			NetworkClient.OnMessageRecieved = OnNetworkMessageReceived;

			SendBuffer = new byte[ImpunityConstants.MaxMessageSize];
		}

		public static RemoteGameConnection MakeTCPRemoteConnection(IPEndPoint serverEndpoint, ImpunityOptions options = null)
		{
			return new RemoteGameConnection(ImpunityTCPClient.MakeTCPClient(serverEndpoint, options));
		}

		public override void Connect(ImpunityCallback onComplete)
		{
			NetworkClient.Connect((ImpunityError err) =>
			{
				NoOpAction connectAction = new NoOpAction(onComplete);

				if (err != null)
				{
					connectAction.Error = err;
					CompletedActions.Enqueue(connectAction);
					return;
				}

				Running = true;
				NetworkWriterThread = new Thread(new ThreadStart(NetworkWriterThreadMain));
				NetworkWriterThread.IsBackground = true;
				NetworkWriterThread.Name = "Network writer";
				NetworkWriterThread.Start();

				CompletedActions.Enqueue(connectAction);
			});
		}


		public override void Dispose()
		{
			PendingSend.CompleteAdding();

			NetworkClient.Dispose();
		}

		public override void Update()
		{
			while (CompletedActions.TryDequeue(out GameStateActionBase action))
			{
				try
				{
					action.InvokeOnCompleteCallback();
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError(e, "Exception in action results callback");
				}
			}

		}

		private void NetworkWriterThreadMain()
		{

			while (Running)
			{
				GameStateActionBase action = null;

				try
				{
					action = PendingSend.Take();
				}
				catch (InvalidOperationException)
				{
					// Pending actions queue was closed
					return;
				}

				try
				{
					SendMessage(action);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError(e, "Exception in remote connection send attempt");
				}
			}
		}


		private void SendMessage(GameStateActionBase action)
		{
			BsonDocument requestBson = action.SerializeRequest();

			ushort flags = 0;
			if (!action.HasCallback())
			{
				flags |= ImpunityMessageFlags.NO_REPLY;
			}
			else
            {
				AwaitingReceive.Enqueue(action);
			}

			ArraySegment<byte> encodedMessage = ImpunityNetworkingUtil.WriteMessage(SendBuffer, 0, flags, action.GetActionType(), requestBson);

			NetworkClient.SendGuaranteedMessage(encodedMessage);

		}

		// On dotnet internal socket thread
		private void OnNetworkMessageReceived(ArraySegment<byte> messageBytes)
		{
			MessageStruct msg;

			ImpunityNetworkingUtil.ReadMessage(messageBytes, out msg);

			switch (msg.MessageType)
			{
				case ServerMessageTypes.REPLY:
					{
						OnReplyMessage(msg.MessageId, msg.Body);
						break;
					}
				default:
					{
						ImpunityLogger.LogError("Got unknown message type: " + msg.MessageType);
						break;
					}
			}
		}


		// On dotnet internal socket thread
		private void OnNetworkErrorReceived(ImpunityError error)
		{
			CompletedActions.Enqueue(new NoOpAction(OnNetworkError));
		}


		private void OnReplyMessage(ushort messageId, BsonDocument replyBody)
		{
			GameStateActionBase action;
			if (!AwaitingReceive.TryDequeue(out action))
			{
				ImpunityLogger.LogError("Got response with id " + messageId + " when we weren't expecting any responses");
				return;
			}

			//if (action.MessageId != messageId)
			//{
			//	ImpunityLogger.LogError("Got response with id " + messageId + " when we were expecting response " + action.MessageId);
			//	return;
			//}

			try
			{
				action.DeserializeResults(replyBody);
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError(e, "Error deserializing reply message body for message type " + action.GetActionType() + " id " + messageId);
				return;
			}

			// Ready for callback!
			CompletedActions.Enqueue(action);
		}



		public override void DoAction(GameStateActionBase action)
        {
			PendingSend.Add(action);

		}

	}

}