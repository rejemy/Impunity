using System;

using System.Threading.Tasks;

namespace Impunity.Networking
{
	public delegate void ImpunityServerMessageHandler(IImpunityNetworkServerClientContext context, ArraySegment<byte> messageBytes);
	public delegate void ImpunityServerErrorCallback(IImpunityNetworkServerClientContext client, ImpunityErrorResponse err);
	public delegate void ImpunityServerClientContextCallback(IImpunityNetworkServerClientContext client);
	
	public interface IImpunityNetworkServerClientContext : IDisposable
	{
		ImpunityServerMessageHandler OnMessageRecieved { get; set; }
		ImpunityServerErrorCallback OnNetworkError { get; set; }
		ImpunityServerClientContextCallback OnClientDisconnected { get; set; }

		string GetAddress();
		bool SupportsUnguaranteed();

		// Writes are not thread safe, caller must ensure that send is not called while a previous send is still completing
		// Must be prefixed by a 4 byte length header
		Task SendGuaranteedMessageAsync(ArraySegment<byte> messageBytes);
		// Writes are not thread safe, caller must ensure that send is not called while a previous send is still completing
		// Must be prefixed by a 4 byte length header
		Task SendUnguaranteedMessageAsync(ArraySegment<byte> messageBytes);

		void Disconnect();
	}

}