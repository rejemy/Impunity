using System;

namespace Impunity.Networking
{
	public delegate void NetworkMessageHandler(byte[] buffer, int length);
	public delegate void NetworkErrorHandler(string error);

	public interface IImpunityClient : IDisposable
	{
		NetworkMessageHandler OnMessageRecieved { get; set; }
		NetworkErrorHandler OnNetworkError { get; set; }

		void Connect();
		void Disconnect();

		bool SupportsUnguaranteed();

		void SendGuaranteedMessage(byte[] buffer, int offset, int length);
		void SendUnguaranteedMessage(byte[] buffer, int offset, int length);

	}

}