using System;

namespace Impunity.Networking
{
	/// <summary>Callback for when a complete binary message is received from the server.</summary>
	public delegate void ImpunityClientMessageHandler(ArraySegment<byte> messageBytes);

	/// <summary>Client-side network transport interface. Implemented by TCP client, WebSocket client, etc.</summary>
	public interface IImpunityNetworkClient : IDisposable
	{
		/// <summary>Server-assigned connection identifier, available after successful connection.</summary>
		public string ConnectionId { get; }
		/// <summary>Called when a complete framed message is received from the server.</summary>
		ImpunityClientMessageHandler OnMessageRecieved { get; set; }
		/// <summary>Called on network errors (connection lost, timeout, etc.).</summary>
		ImpunityCallback OnNetworkError { get; set; }
		/// <summary>Called when the server closes the connection. The int parameter is a reason code.</summary>
		Action<int> OnDisconnectedByServer { get; set; }

		/// <summary>Initiates an async connection to the server. Calls <paramref name="onComplete"/> with null error on success.</summary>
		void Connect(ImpunityCallback onComplete);
		/// <summary>Closes the connection.</summary>
		void Disconnect();

		/// <summary>Whether this transport supports unguaranteed (UDP-like) delivery.</summary>
		bool SupportsUnguaranteed { get; }

		/// <summary>Sends a length-prefixed binary message reliably.</summary>
		void SendGuaranteedMessage(ArraySegment<byte> messageBytes);
		/// <summary>Sends a binary message via the unguaranteed channel if available, other wise will send guaranteed.</summary>
		void SendUnguaranteedMessage(ArraySegment<byte> messageBytes);

	}

}