using System;

using System.Threading.Tasks;

namespace Impunity.Networking
{
	/// <summary>Callback for when a complete binary message is received from a client.</summary>
	public delegate void ImpunityServerMessageHandler(IImpunityNetworkServerClientContext context, ArraySegment<byte> messageBytes);
	/// <summary>Callback for network-level errors on a client connection.</summary>
	public delegate void ImpunityServerErrorCallback(IImpunityNetworkServerClientContext client, ImpunityErrorResponse err);
	/// <summary>Callback for when a client connection is closed.</summary>
	public delegate void ImpunityServerClientContextCallback(IImpunityNetworkServerClientContext client);

	/// <summary>Represents a single client connection on the server side. Protocol-agnostic interface implemented by TCP, WebSocket, etc. Sends are NOT thread-safe; callers must serialize access (e.g., via a semaphore).</summary>
	public interface IImpunityNetworkServerClientContext : IDisposable
	{
		/// <summary>Called when a complete framed message is received from this client.</summary>
		ImpunityServerMessageHandler? OnMessageRecieved { get; set; }
		/// <summary>Called on network errors (broken pipe, timeout, etc.).</summary>
		ImpunityServerErrorCallback? OnNetworkError { get; set; }
		/// <summary>Called when the client disconnects (gracefully or not).</summary>
		ImpunityServerClientContextCallback? OnClientDisconnected { get; set; }

		/// <summary>Server-assigned unique identifier for this connection.</summary>
		string ConnectionId { get; }
		/// <summary>Remote IP address or endpoint string for logging.</summary>
		string RemoteAddress { get; }
		/// <summary>Whether this transport supports unguaranteed (UDP-like) delivery.</summary>
		bool SupportsUnguaranteed { get; }

		/// <summary>Sends a length-prefixed binary message reliably (TCP/WebSocket). Not thread-safe.</summary>
		Task SendGuaranteedMessageAsync(ArraySegment<byte> messageBytes);
		/// <summary>Sends a binary message via the unguaranteed channel if available, otherwise falls back to guaranteed. Not thread-safe.</summary>
		Task SendUnguaranteedMessageAsync(ArraySegment<byte> messageBytes);

		/// <summary>Closes the connection and releases resources.</summary>
		void Disconnect();
	}

}