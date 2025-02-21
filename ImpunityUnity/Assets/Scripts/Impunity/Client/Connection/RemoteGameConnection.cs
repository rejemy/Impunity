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
		public ImpunityCallback OnNetworkError { get; set; }

		private BlockingCollection<GameStateActionBase> PendingSend;
		private ConcurrentQueue<GameStateActionBase> AwaitingReceive;

		private string GameId;
		private string GamePassword;
		private ImpunityOptions Options;
		private IImpunityClient NetworkClient;
		private Thread NetworkWriterThread;
		private bool Running;

		private byte[] SendBuffer;

		public RemoteGameConnection(IImpunityClient networkClient, string gameId, string gamePassword, GameStateFormat format, ImpunityOptions options, ClientEntityManager em) : base(format, em)
		{
			PendingSend = new BlockingCollection<GameStateActionBase>();
			AwaitingReceive = new ConcurrentQueue<GameStateActionBase>();

			GameId = gameId;
			GamePassword = gamePassword;

			if (options == null)
			{
				options = new ImpunityOptions();
			}
			Options = options;
			NetworkClient = networkClient;
			NetworkClient.OnNetworkError = OnNetworkErrorReceived;
			NetworkClient.OnMessageRecieved = OnNetworkMessageReceived;
			NetworkClient.OnDisconnectedByServer = OnDisconnectedByServer;

			SendBuffer = new byte[ImpunityConstants.MaxMessageSize];

			ConnectionId = "RemoteConnection_" + Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Substring(0, 8);
			
		}

		public static RemoteGameConnection MakeTCPRemoteConnection(IPEndPoint serverEndpoint, string gameId, string gamePassword, GameStateFormat format, ImpunityOptions options = null, ClientEntityManager em = null)
		{
			if (options == null)
			{
				options = new ImpunityOptions();
			}

			return new RemoteGameConnection(ImpunityTCPClient.MakeTCPClient(serverEndpoint, options), gameId, gamePassword, format, options, em);
		}

		public override void Connect(ImpunityCallback onComplete)
		{
			NetworkClient.Connect((ImpunityErrorResponse err) =>
			{
				if (err != null)
				{
					NoOpAction connectAction = new NoOpAction(onComplete);
					connectAction.Error = err;
					CompletedActions.Enqueue(connectAction);
					return;
				}

				Running = true;
				NetworkWriterThread = new Thread(new ThreadStart(NetworkWriterThreadMain));
				NetworkWriterThread.IsBackground = true;
				NetworkWriterThread.Name = "Network writer";
				NetworkWriterThread.Start();

				EstablishConnection(GameId, GamePassword, LocalFormat, onComplete);
			});
		}


		public override void Dispose()
		{
			PendingSend.CompleteAdding();

			NetworkClient.Dispose();
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
					ImpunityLogger.LogError("Exception in remote connection send attempt", e);
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

			action.Cleanup();
		}

		// On dotnet internal socket thread
		private void OnNetworkMessageReceived(ArraySegment<byte> messageBytes)
		{
			MessageStruct msg;

			ImpunityNetworkingUtil.ReadMessage(messageBytes, out msg);

			// Reply message
			if (msg.MessageType == (ushort)ServerActionType.CLIENT_REPLY)
            {
				HandleReplyMessage(msg.MessageId, msg.Body);
				return;
			}

			// Server message
			Type messageActionClassType = ServerActionFactory.GetActionClassType(msg.MessageType);

			BsonMapper mapper = ImpunityUtil.GetBsonMapper();
			ServerActionBase action = (ServerActionBase)mapper.ToObject(messageActionClassType, msg.Body);

			// Ready for callback!
			CompletedActions.Enqueue(action);
		}


		// On dotnet internal socket thread
		private void OnNetworkErrorReceived(ImpunityErrorResponse error)
		{
			CompletedActions.Enqueue(new NoOpAction(OnNetworkError));
		}

		private void OnDisconnectedByServer(int reason)
		{
			ImpunityLogger.LogInformation("Disconnected by server with code " + reason);
		}

		private void HandleReplyMessage(ushort messageId, BsonDocument replyBody)
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
				ImpunityLogger.LogError("Error deserializing reply message body for message type " + action.GetActionType() + " id " + messageId, e);
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