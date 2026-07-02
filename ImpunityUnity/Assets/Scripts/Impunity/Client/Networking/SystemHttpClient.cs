using System;
using System.Net.Http;

namespace Impunity.Networking
{
	/// <summary>
	/// Default <see cref="IImpunityHttpClient"/> implementation backed by <see cref="System.Net.Http.HttpClient"/>.
	/// Used automatically outside of Unity. Callbacks are invoked on a thread-pool thread (there is no Unity
	/// main-thread guarantee); callers that need main-thread dispatch should marshal it themselves.
	/// </summary>
	public class SystemHttpClient : IImpunityHttpClient
	{
		// A single shared HttpClient is the recommended usage pattern (avoids socket exhaustion).
		static readonly HttpClient SharedClient = new HttpClient();

		/// <inheritdoc/>
		public void Get(string url, ImpunityCallback<string> onComplete)
		{
			SharedClient.GetStringAsync(url).ContinueWith(task =>
			{
				if (task.IsFaulted)
				{
					Exception ex = task.Exception?.GetBaseException() ?? new Exception("Unknown HTTP error");
					try
					{
						onComplete(new ImpunityErrorResponse(ImpunityErrorCode.ClientUnableToConnectError, ex.Message), null!);
					}
					catch (Exception cbEx)
					{
						ImpunityLogger.LogError("Exception in SystemHttpClient error callback", cbEx);
					}
					return;
				}

				try
				{
					onComplete(null, task.Result);
				}
				catch (Exception cbEx)
				{
					ImpunityLogger.LogError("Exception in SystemHttpClient callback", cbEx);
				}
			});
		}
	}
}
