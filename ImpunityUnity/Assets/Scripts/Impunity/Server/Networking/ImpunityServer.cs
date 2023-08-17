using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

using UltraLiteDB;

using Impunity.GameState;
using System.Net;

namespace Impunity.Networking
{


    internal class ServerSideNetworkConnectionProxy : IServerSideConnectionProxy
    {
        IImpunityNetworkServerClientContext ClientContext;
        ImpunityServer Server;
        byte[] SendBuffer;
        Semaphore SendLock;

        public string ConnectionId { get { return "NetworkConnection_" + ClientContext.GetAddress(); } }
        public GameStateReplicant ConnectionReplicant { get; set; }

        public bool SupportsUnguaranteed()
        {
            return ClientContext.SupportsUnguaranteed();
        }

        public ServerSideNetworkConnectionProxy(ImpunityServer server, IImpunityNetworkServerClientContext clientContext)
        {
            Server = server;
            ClientContext = clientContext;
            SendBuffer = new byte[ImpunityConstants.MaxMessageSize];
            SendLock = new Semaphore(1, 1);

            clientContext.OnMessageRecieved = ClientMessageReceived;
            clientContext.OnNetworkError = ClientNetworkError;
            clientContext.OnClientDisconnected = ClientDisconnected;

        }

        // Called on socket thread
        private void ClientMessageReceived(IImpunityNetworkServerClientContext context, ArraySegment<byte> messageBytes)
        {
            MessageStruct msg;

            ImpunityNetworkingUtil.ReadMessage(messageBytes, out msg);

            Type messageActionClassType = ClientActionFactory.GetActionClassType(msg.MessageType);

            BsonMapper mapper = ImpunityNetworkingUtil.GetBsonMapper();
            GameStateActionBase action = (GameStateActionBase)mapper.ToObject(messageActionClassType, msg.Body);

            action.Origin = this;

            action.ResultsExpected = (msg.Flags & ImpunityMessageFlags.NO_REPLY) == 0;

            Server.GameState.QueueAction(action);
        }

        // Called on writer thread
        public void SendMessage(ushort messageType, bool guaranteed, BsonDocument results)
        {
            ArraySegment<byte> encodedMessage;

            // Lock send buffer (or wait for it to be available)
            SendLock.WaitOne();

            encodedMessage = ImpunityNetworkingUtil.WriteMessage(SendBuffer, 0, 0, messageType, results);

            if (guaranteed)
            {
                ClientContext.SendGuaranteedMessageAsync(encodedMessage).ContinueWith(OnDataWritten);
            }
            else
            {
                ClientContext.SendUnguaranteedMessageAsync(encodedMessage).ContinueWith(OnDataWritten);
            }
            
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

        // Called on server thread
        public void ReportActionResult(GameStateActionBase action)
        {
            // Don't send on server thread, queue for send on network writer thread
            Server.ActionCompleted(action);
        }

        // Called on server thread
        public void SendMessageToClient(ServerActionBase message)
        {
            // Don't send on server thread, queue for send on network writer thread
            message.Origin = this;
            Server.ActionCompleted(message);
        }

        // Called on socket thread
        private void ClientNetworkError(IImpunityNetworkServerClientContext client, ImpunityError err)
        {
            ImpunityLogger.LogError("client network error: " + err.Message);

            // Todo - close connection?
        }

        // Called on socket thread
        private void ClientDisconnected(IImpunityNetworkServerClientContext client)
        {
            Server.GameState.ConnectionClosed(this);
        }
    }

    public class ImpunityServer : IDisposable, IGameStateListener
    {
        public GameStateServer GameState { get; private set; }
        IImpunityNetworkServer NetworkServer;

        BlockingCollection<GameStateActionBase> PendingWrite;

        Thread NetworkWriterThread;
        bool Running;

        public IPEndPoint TCPEndpoint { get; private set; }

        public ImpunityServer(GameStateServer gameState, IImpunityNetworkServer networkServer)
        {
            GameState = gameState;
            NetworkServer = networkServer;
            PendingWrite = new BlockingCollection<GameStateActionBase>();

            OnGameSummaryChanged(GameState.GetGameSummary());

            NetworkServer.OnClientConnected = ClientConnected;

            GameState.AddListener(this);

        }

        public static ImpunityServer MakeTCPServer(GameStateServer gameState, ImpunityOptions options = null)
        {
            ImpunityTCPServer tcpserver = new ImpunityTCPServer(options);
            ImpunityServer server = new ImpunityServer(gameState, tcpserver);
 
            return server;
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

            TCPEndpoint = NetworkServer.Listen();
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
            ServerSideNetworkConnectionProxy clientInfo = (ServerSideNetworkConnectionProxy)action.Origin;

            
            if (action is ServerActionBase)
            {
                // Server originated message
                BsonDocument message = action.SerializeRequest();
                clientInfo.SendMessage(action.GetActionType(), ((ServerActionBase)action).Guaranteed, message);
            }
            else
            {
                // Reply to client action
                BsonDocument results = action.SerializeResults();
                clientInfo.SendMessage((ushort)ServerActionType.CLIENT_REPLY, true, results);
            }

            
        }

        // Called on socket thread
        private void ClientConnected(IImpunityNetworkServerClientContext client)
        {
            ServerSideNetworkConnectionProxy proxy = new ServerSideNetworkConnectionProxy(this, client);
            client.ClientInfo = proxy;
            GameState.ConnectionOpened(proxy);
        }

        

        // called on game server thread
        internal void ActionCompleted(GameStateActionBase action)
        {
            PendingWrite.Add(action);
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

