using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

using UltraLiteDB;

using Impunity.GameState;


namespace Impunity.Networking
{


    public class ClientContextInfo
    {
        byte[] SendBuffer;
        Semaphore SendLock;

        public ClientContextInfo()
        {
            SendBuffer = new byte[ImpunityConstants.MaxMessageSize];
            SendLock = new Semaphore(1, 1);
        }

        // Called on writer thread
        public void SendActionResults(IImpunityNetworkServerClientContext clientContext, BsonDocument results)
        {
            ArraySegment<byte> encodedMessage;

            // Lock send buffer (or wait for it to be available)
            SendLock.WaitOne();

            encodedMessage = ImpunityNetworkingUtil.WriteMessage(SendBuffer, 0, 0, ServerMessageTypes.REPLY, results);

            clientContext.SendGuaranteedMessageAsync(encodedMessage).ContinueWith(OnDataWritten);
        }

        // Called on TCP socket thread
        private void OnDataWritten(Task writeTask)
        {
            if (!writeTask.IsCompletedSuccessfully)
            {
                ImpunityLogger.LogError("Error writing to socket: " + writeTask.Exception?.Message);
                writeTask.Dispose();

                // Close socket or something?

                return;
            }

            writeTask.Dispose();

            // Unlock send buffer
            SendLock.Release();
        }
    }

    public class ImpunityServer : IDisposable, IGameStateListener
    {
        public GameStateServer GameState { get; private set; }
        IImpunityNetworkServer NetworkServer;

        BlockingCollection<GameStateActionBase> PendingWrite;

        Thread NetworkWriterThread;
        bool Running;

        //ConcurrentDictionary<string, ImpunityServerClientContext> Clients;

        public ImpunityServer(GameStateServer gameState, IImpunityNetworkServer networkServer)
        {
            GameState = gameState;
            NetworkServer = networkServer;
            PendingWrite = new BlockingCollection<GameStateActionBase>();
            //Clients = new ConcurrentDictionary<string, ImpunityServerClientContext>();

            OnGameSummaryChanged(GameState.GetGameSummary());

            NetworkServer.OnClientConnected = ClientConnected;
            NetworkServer.OnClientDisconnected = ClientDisconnected;

            GameState.AddListener(this);

        }

        public static ImpunityServer MakeTCPServer(GameStateServer gameState, ImpunityOptions options = null)
        {
            return new ImpunityServer(gameState, new ImpunityTCPServer(options));
        }

        public void OnGameSummaryChanged(BsonDocument summary)
        {
            if (summary == null)
            {
                return;
            }
            byte[] summaryBytes = BsonWriter.Serialize(summary);
            NetworkServer.SetGameSummaryBytes(new ArraySegment<byte>(summaryBytes));
        }

        public void Start()
        {
            Running = true;
            NetworkWriterThread = new Thread(new ThreadStart(WriterThreadMain));
            NetworkWriterThread.IsBackground = false;
            NetworkWriterThread.Name = "Network write";
            NetworkWriterThread.Start();

            NetworkServer.Listen();
        }

        private void WriterThreadMain()
        {
            while (Running)
            {
                GameStateActionBase action = null;

                try
                {
                    action = PendingWrite.Take();
                }
                catch (InvalidOperationException)
                {
                    // Pending actions queue was closed
                    break;
                }

                try
                {
                    SendActionResults(action);
                }
                catch (Exception e)
                {
                    ImpunityLogger.LogError(e, "Exception sending action result over network");
                }
                
            }

            PendingWrite.Dispose();
            PendingWrite = null;
        }

        private void SendActionResults(GameStateActionBase action)
        {
            IImpunityNetworkServerClientContext clientContext = (IImpunityNetworkServerClientContext)action.Context;
            ClientContextInfo clientInfo = (ClientContextInfo)clientContext.ClientInfo;

            BsonDocument results = action.SerializeResults();
            clientInfo.SendActionResults(clientContext, results);
        }

        // Called on socket thread
        private void ClientConnected(IImpunityNetworkServerClientContext client)
        {
            client.OnMessageRecieved = ClientMessageReceived;
            client.OnNetworkError = ClientNetworkError;
            client.ClientInfo = new ClientContextInfo();

            //ImpunityServerClientContext context = new ImpunityServerClientContext(client);
            //Clients[context.Address] = context;
        }

        // Called on socket thread
        private void ClientMessageReceived(IImpunityNetworkServerClientContext context, ArraySegment<byte> messageBytes)
        {
            MessageStruct msg;

            ImpunityNetworkingUtil.ReadMessage(messageBytes, out msg);

            Type messageActionClassType = GameActionFactory.GetActionClassType(msg.MessageType);

            BsonMapper mapper = ImpunityNetworkingUtil.GetBsonMapper();
            GameStateActionBase action = (GameStateActionBase)mapper.ToObject(messageActionClassType, msg.Body);
            action.Context = context;

            if ((msg.Flags & ImpunityMessageFlags.NO_REPLY) == 0)
            {
                // Reply expected
                action.OnCompleteHandler = ActionCompleted;
            }

            GameState.QueueAction(action);
        }

        // called on game server thread
        private void ActionCompleted(GameStateActionBase action)
        {
            PendingWrite.Add(action);
        }

        // Called on socket thread
        private void ClientNetworkError(IImpunityNetworkServerClientContext client, ImpunityError err)
        {
            ImpunityLogger.LogError("client network error: " + err.Message);
        }

        // Called on socket thread
        private void ClientDisconnected(IImpunityNetworkServerClientContext client)
        {
            //ImpunityServerClientContext context;
            //if (!Clients.TryRemove(client.GetAddress(), out context))
            //{
            //    ImpunityLogger.LogError("Got a client disconnect for a client we haven't heard about: " + client.GetAddress());
            //    return;
            //}
        }

        public void Dispose()
        {
            NetworkServer?.Dispose();
            NetworkServer = null;

            Running = false;
            PendingWrite?.CompleteAdding();
            NetworkWriterThread?.Join();
            NetworkWriterThread = null;

            GameState.RemoveListener(this);

        }
    }

}

