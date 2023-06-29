using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using UltraLiteDB;

using Impunity.GameState;



namespace Impunity.Networking
{
    public delegate void ImpunityServerMessageHandler(IImpunityNetworkServerClientContext context, ArraySegment<byte> messageBytes);
    public delegate void ImpunityServerErrorCallback(IImpunityNetworkServerClientContext client, ImpunityError err);
    public delegate void ImpunityServerClientContextCallback(IImpunityNetworkServerClientContext client);
    
    public interface IImpunityNetworkServerClientContext : IDisposable
    {
        ImpunityServerMessageHandler OnMessageRecieved { get; set; }
        ImpunityServerErrorCallback OnNetworkError { get; set; }
        ImpunityServerClientContextCallback OnClientDisconnected { get; set; }
        object ClientInfo { get; set; }

        string GetAddress();
        bool SupportsUnguaranteed();

        void Listen();

        // Writes are not thread safe, caller must ensure that send is not called while a previous send is still completing
        // Must be prefixed by a 4 byte length header
        Task SendGuaranteedMessageAsync(ArraySegment<byte> messageBytes);
        // Writes are not thread safe, caller must ensure that send is not called while a previous send is still completing
        // Must be prefixed by a 4 byte length header
        Task SendUnguaranteedMessageAsync(ArraySegment<byte> messageBytes);

        void Disconnect();
    }

    public interface IImpunityNetworkServer : IDisposable
    {
        ImpunityServerClientContextCallback OnClientConnected { get; set; }
        ImpunityServerClientContextCallback OnClientDisconnected { get; set; }

        void SetGameSummaryBytes(ArraySegment<byte> summaryBytes);

        IEnumerable<IImpunityNetworkServerClientContext> ConnectedClients();

        void Listen();
    }

}