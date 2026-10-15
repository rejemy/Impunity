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

	/// <summary>Represents a single client connection on the server side. Protocol-agnostic interface implemented by TCP, WebSocket, etc.</summary>
	/// <remarks>
	/// Send threading, as <see cref="ServerSideNetworkConnectionProxy"/> uses it: at most one
	/// <see cref="SendGuaranteedMessageAsync"/> is in flight at a time, and the next is started from the previous one's
	/// completion, so an implementation may return a task that stays pending while the peer isn't reading.
	/// <see cref="SendUnguaranteedMessageAsync"/> is only called when <see cref="SupportsUnguaranteed"/> is true, never
	/// concurrently with itself, but possibly while a guaranteed send is in flight.
	/// </remarks>
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

		/// <summary>Sends reliably (TCP/WebSocket). <paramref name="messageBytes"/> holds one or more complete
		/// length-prefixed messages back to back; a transport that needs per-message boundaries (WebSocket) splits them on
		/// the length prefixes. The array is only valid until the returned task completes.</summary>
		Task SendGuaranteedMessageAsync(ArraySegment<byte> messageBytes);
		/// <summary>Sends one message via the unguaranteed channel (UDP). Only called when <see cref="SupportsUnguaranteed"/>
		/// is true; may overlap a guaranteed send in flight.</summary>
		Task SendUnguaranteedMessageAsync(ArraySegment<byte> messageBytes);

		/// <summary>Closes the connection and releases resources.</summary>
		void Disconnect();
	}

}
