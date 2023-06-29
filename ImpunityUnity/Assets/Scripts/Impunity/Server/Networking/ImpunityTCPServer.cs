using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text;


namespace Impunity.Networking
{

    public class ImpunityTCPServerClientContext : IImpunityNetworkServerClientContext
    {
        public ImpunityServerMessageHandler OnMessageRecieved { get; set; }
        public ImpunityServerErrorCallback OnNetworkError { get; set; }
        public ImpunityServerClientContextCallback OnClientDisconnected { get; set; }
        public object ClientInfo { get; set; }

        ImpunityTCPServer Server;
        TcpClient Client;
        NetworkStream ClientStream;

        int MaxBytesToReceive = ImpunityConstants.MaxMessageSize;
        byte[] ReceiveBuffer;
        int BytesReceived = 0;

        public EndPoint RemoteEndpoint { get; private set; }


        public ImpunityTCPServerClientContext(ImpunityTCPServer server, TcpClient client)
        {
            Server = server;
            Client = client;
            ClientStream = client.GetStream();
            ReceiveBuffer = new byte[ImpunityConstants.MaxMessageSize];
            RemoteEndpoint = Client.Client.RemoteEndPoint;
        }

        public bool SupportsUnguaranteed()
        {
            return false;
        }

        public string GetAddress()
        {
            return RemoteEndpoint.ToString();
        }

        public void Listen()
        {
            ClientStream.ReadAsync(ReceiveBuffer, 0, ImpunityConstants.MaxMessageSize)
                .ContinueWith(OnDataRead);
        }

        // On socket thread
        private void OnDataRead(Task<int> readTask)
        {
            if (!readTask.IsCompletedSuccessfully)
            {
                ImpunityLogger.LogError(readTask.Exception, "Error reading socket");

                try
                {
                    OnNetworkError?.Invoke(this, new ImpunityError(readTask.Exception.Message));
                }
                catch(Exception e)
                {
                    ImpunityLogger.LogError(e, "Exception in TCP socket error handler");
                }

                readTask.Dispose();
                return;
            }

            int bytesRead = readTask.Result;
            readTask.Dispose();

            if (bytesRead == 0 || !Client.Connected)
            {
                Disconnect();

                return;
            }

            HandleDataRead(bytesRead);

            // And finish by going back to reading
            ClientStream.ReadAsync(ReceiveBuffer, 0, MaxBytesToReceive)
                .ContinueWith(OnDataRead);

        }

        private void HandleDataRead(int bytesRead)
        {
            BytesReceived += bytesRead;

            if (BytesReceived < 4)
            {
                // Didn't read enough to get a size, this is probably some kind of error
                ImpunityLogger.LogWarning("Only read " + BytesReceived + " from the TCP socket");
                MaxBytesToReceive = ImpunityConstants.MaxMessageSize - BytesReceived;
                return;
            }

            int messageLength = ImpunityNetworkingUtil.GetMessageLength(ReceiveBuffer);
            if (BytesReceived < messageLength)
            {
                ImpunityLogger.LogDebug("Got partial message: " + BytesReceived + " / " + messageLength);
                MaxBytesToReceive = messageLength - BytesReceived;
                return;
            }

            // We have the whole message, handle it
            try
            {
                OnMessageRecieved?.Invoke(this, new ArraySegment<byte>(ReceiveBuffer, 0, messageLength));
            }
            catch (Exception e)
            {
                ImpunityLogger.LogError(e, "Exception in TCP socket message handler");
            }
        }

        public Task SendGuaranteedMessageAsync(ArraySegment<byte> messageBytes)
        {
            return ClientStream.WriteAsync(messageBytes.Array, messageBytes.Offset, messageBytes.Count);
        }

        public Task SendUnguaranteedMessageAsync(ArraySegment<byte> messageBytes)
        {
            return ClientStream.WriteAsync(messageBytes.Array, messageBytes.Offset, messageBytes.Count);
        }


        public void Disconnect()
        {
            try
            {
                Client?.Close();
                Client?.Dispose();
                Client = null;
            }
            catch(Exception e)
            {
                ImpunityLogger.LogError(e, "Exception closing TCP socket");
            }

            Server.ClientDisconnected(this);

            try
            {
                OnClientDisconnected?.Invoke(this);
            }
            catch (Exception e)
            {
                ImpunityLogger.LogError(e, "Exception in TCP socket disconnect handler");
            }
        }

        public void Dispose()
        {
            Disconnect();
        }

    }

    public class ImpunityTCPServer : IImpunityNetworkServer
    {
        public ImpunityServerClientContextCallback OnClientConnected { get; set; }
        public ImpunityServerClientContextCallback OnClientDisconnected { get; set; }

        ImpunityOptions Options;

        Thread TCPListenerThread;
        TcpListener TCPSocket;

        Thread UDPListenerThread;
        UdpClient ServerUdpSocket;

        bool Running;

        ArraySegment<byte> AnnouncePacket;
        byte[] SearchPacket;

        ConcurrentDictionary<EndPoint, ImpunityTCPServerClientContext> Clients;

        public ImpunityTCPServer(ImpunityOptions options = null)
        {
            if (options == null)
            {
                options = new ImpunityOptions();
            }
            Options = options;

            AnnouncePacket = new ArraySegment<byte>(new byte[ImpunityConstants.MaxMessageSize]);
            AnnouncePacket = ImpunityNetworkingUtil.MakeBroadcastPacket(AnnouncePacket.Array, ImpunityConstants.ServerAnnouncePacketHeader + Options.GameTypeCode + ":", null, 0);

            Running = true;

            Clients = new ConcurrentDictionary<EndPoint, ImpunityTCPServerClientContext>();

        }

        public void SetGameSummaryBytes(ArraySegment<byte> summary)
        {
            AnnouncePacket = ImpunityNetworkingUtil.MakeBroadcastPacket(AnnouncePacket.Array, ImpunityConstants.ServerAnnouncePacketHeader + Options.GameTypeCode + ":", summary.Array, summary.Count);
        }

        public IEnumerable<IImpunityNetworkServerClientContext> ConnectedClients()
        {
            return Clients.Values;
        }

        public void Listen()
        {
            StartTcpListener();

            if (Options.LANDiscoverable)
            {
                SearchPacket = Encoding.UTF8.GetBytes(ImpunityConstants.ServerSearchPacketHeader + Options.GameTypeCode + ":");

                

                StartBroadcastListen();
            }
        }

        private void StartTcpListener()
        {
            TCPListenerThread = new Thread(new ThreadStart(TCPListener));
            TCPListenerThread.IsBackground = true;
            TCPListenerThread.Name = "TCP listener";
            TCPListenerThread.Start();
        }

        private void TCPListener()
        {
            TCPSocket = null;

            try
            {
                TCPSocket = new TcpListener(IPAddress.Any, Options.ServerPort);
                //TCPSocket.AllowNatTraversal(true);
                TCPSocket.Start();

                ImpunityLogger.LogInformation("Server TCP Socket listener started");

                while (TCPSocket != null)
                {
                    TcpClient client = TCPSocket.AcceptTcpClient();

                    ImpunityTCPServerClientContext context = new ImpunityTCPServerClientContext(this, client);
                    Clients[context.RemoteEndpoint] = context;
                    ImpunityLogger.LogInformation("Client connected");

                    context.Listen();

                    try
                    {
                        OnClientConnected?.Invoke(context);
                    }
                    catch (Exception e)
                    {
                        ImpunityLogger.LogError(e, "Exception in TCP client connected callback");
                    }
                }


            }
            catch (SocketException e)
            {
                if (!Running)
                {
                    ImpunityLogger.LogInformation("Server TCP Socket listener closed");
                    return;
                }

                ImpunityLogger.LogError(e, "TCP Socket error:");
            }
            finally
            {
                if (TCPSocket != null)
                {
                    TCPSocket.Stop();
                    TCPSocket = null;
                }
            }
        }

        internal void ClientDisconnected(ImpunityTCPServerClientContext context)
        {
            if (!Clients.TryRemove(context.RemoteEndpoint, out _))
            {
                ImpunityLogger.LogError("Got a client disconnect for a client we haven't heard about: " + context.RemoteEndpoint.ToString());
                return;
            }

            try
            {
                OnClientDisconnected?.Invoke(context);
            }
            catch (Exception e)
            {
                ImpunityLogger.LogError(e, "Exception in TCP client disconnected callback");
            }
        }

        private void StartBroadcastListen()
        {
            UDPListenerThread = new Thread(new ThreadStart(UDPListener));
            UDPListenerThread.IsBackground = true;
            UDPListenerThread.Name = "UDP server";
            UDPListenerThread.Start();
        }

        private void StopBroadcastListen()
        {
            if (ServerUdpSocket == null)
            {
                return;
            }

            UdpClient socket = ServerUdpSocket;
            ServerUdpSocket = null;
            socket.Close();
            socket.Dispose();

        }

        private void UDPListener()
        {
            ServerUdpSocket = null;

            try
            {
                ServerUdpSocket = new UdpClient(Options.ServerPort);
                ServerUdpSocket.EnableBroadcast = true;
                //ServerUdpSocket.AllowNatTraversal(true);

                ImpunityLogger.LogInformation("Server UDP Socket listener started");

                SendServerAnnounce();

                IPEndPoint groupEP = new IPEndPoint(IPAddress.Any, Options.ServerPort);

                while (ServerUdpSocket != null)
                {
                    byte[] packet = ServerUdpSocket.Receive(ref groupEP);
                    ImpunityLogger.LogInformation("Got bytes");
                    if (ImpunityNetworkingUtil.StartsWith(packet, SearchPacket))
                    {
                        OnSearchPacket();
                    }
                }
            }
            catch (SocketException e)
            {
                if (ServerUdpSocket == null)
                {
                    ImpunityLogger.LogInformation("Server UDP Socket listener closed");
                    return;
                }

                ImpunityLogger.LogError(e, "UDP Socket error:");
            }
            finally
            {
                if (ServerUdpSocket != null)
                {
                    ServerUdpSocket.Dispose();
                    ServerUdpSocket = null;
                }
            }

        }

        private void OnSearchPacket()
        {
            ImpunityLogger.LogDebug("Got search packet");
            SendServerAnnounce();
        }

        private void SendServerAnnounce()
        {
            ImpunityLogger.LogDebug("Sent server announce");
            IPEndPoint broadcastEp = new IPEndPoint(IPAddress.Any, Options.ClientPort);

            ServerUdpSocket.SendAsync(AnnouncePacket.Array, AnnouncePacket.Count, broadcastEp);
        }

        public void Dispose()
        {
            Running = false;

            StopBroadcastListen();

            foreach (ImpunityTCPServerClientContext client in Clients.Values)
            {
                client.Dispose();
            }
            Clients = null;

            TcpListener listener = TCPSocket;
            TCPSocket = null;
            listener.Stop();
        }
    }

}