using System;
using System.Collections;

using UnityEngine;
using UnityEngine.Networking;

using Impunity.Networking;

namespace Impunity.Unity
{
	/// <summary>
	/// <see cref="IImpunityHttpClient"/> implementation that routes HTTP through Unity's <see cref="UnityWebRequest"/>.
	/// Runs the request as a coroutine on a supplied <see cref="MonoBehaviour"/>, so callbacks are delivered on the
	/// Unity main thread. This is the transport to use inside Unity (and the only option under WebGL).
	/// </summary>
	public class ImpunityUnityHttpClient : IImpunityHttpClient
	{
		readonly MonoBehaviour Runner;

		ImpunityUnityHttpClient(MonoBehaviour runner)
		{
			Runner = runner;
		}

		/// <summary>Installs this client as the global Impunity HTTP client, running coroutines on <paramref name="runner"/>.</summary>
		/// <param name="runner">MonoBehaviour used to drive the request coroutines (typically a persistent scene object).</param>
		public static void Setup(MonoBehaviour runner)
		{
			ImpunityHttp.Instance = new ImpunityUnityHttpClient(runner);
		}

		/// <inheritdoc/>
		public void Get(string url, ImpunityCallback<string> onComplete)
		{
			UnityWebRequest request = UnityWebRequest.Get(url);
			Runner.StartCoroutine(SendRequest(request, onComplete));
		}

		static IEnumerator SendRequest(UnityWebRequest request, ImpunityCallback<string> onComplete)
		{
			using (request)
			{
				yield return request.SendWebRequest();

				if (request.error != null)
				{
					onComplete(new ImpunityErrorResponse(ImpunityErrorCode.ClientUnableToConnectError, request.error), null!);
					yield break;
				}

				onComplete(null, request.downloadHandler.text);
			}
		}
	}
}
