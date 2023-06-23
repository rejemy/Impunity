using System;

namespace Impunity.Networking
{
	public delegate void NetworkMessageHandler(byte[] buffer, int length);

	public interface IImpunityClient : IDisposable
	{
		NetworkMessageHandler OnMessageRecieved { get; set; }
		ImpunityCallback OnNetworkError { get; set; }

		void Connect(ImpunityCallback onComplete);
		void Disconnect();

		bool SupportsUnguaranteed();

		void SendGuaranteedMessage(byte[] buffer, int offset, int length);
		void SendUnguaranteedMessage(byte[] buffer, int offset, int length);

	}

}