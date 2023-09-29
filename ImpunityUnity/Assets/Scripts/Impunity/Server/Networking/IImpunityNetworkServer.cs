using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Impunity.GameState;
using UltraLiteDB;

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

		void AddGameServer(GameStateServer game);
		void SetGameSummary(string gameId, BsonDocument summary);
		void SetGameStateFormat(string gameId, int version, string dataChecksum);

		IEnumerable<IImpunityNetworkServerClientContext> ConnectedClients();

		IPEndPoint Listen();
	}

}