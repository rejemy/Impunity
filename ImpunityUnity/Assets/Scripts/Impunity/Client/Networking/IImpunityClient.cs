using System;

namespace Impunity.Networking
{
	public delegate void ImpunityClientMessageHandler(ArraySegment<byte> messageBytes);

	public interface IImpunityClient : IDisposable
	{
		ImpunityClientMessageHandler OnMessageRecieved { get; set; }
		ImpunityCallback OnNetworkError { get; set; }

		void Connect(ImpunityCallback onComplete);
		void Disconnect();

		bool SupportsUnguaranteed();

		// Must be prefixed by a 4 byte length header
		void SendGuaranteedMessage(ArraySegment<byte> messageBytes);
		// Must be prefixed by a 4 byte length header
		void SendUnguaranteedMessage(ArraySegment<byte> messageBytes);

	}

}