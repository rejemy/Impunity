using System;

namespace Impunity.Networking
{
	/// <summary>
	/// Abstraction over a simple asynchronous HTTP client so the library can run both inside Unity
	/// (via <c>UnityWebRequest</c>) and in a standalone .NET process (via <see cref="System.Net.Http.HttpClient"/>).
	/// Implement this to route Impunity's HTTP traffic through a platform-appropriate transport.
	/// </summary>
	public interface IImpunityHttpClient
	{
		/// <summary>
		/// Performs an HTTP GET against <paramref name="url"/> and invokes <paramref name="onComplete"/> with the
		/// response body text, or an <see cref="ImpunityErrorResponse"/> on failure.
		/// </summary>
		/// <param name="url">Absolute URL to request.</param>
		/// <param name="onComplete">Called with the response body on success, or a non-null error on failure. The thread it is called on is implementation-defined (Unity's implementation calls back on the main thread; the standalone implementation may call back on a thread-pool thread).</param>
		void Get(string url, ImpunityCallback<string> onComplete);
	}

	/// <summary>
	/// Static facade providing the active <see cref="IImpunityHttpClient"/>. Defaults to <see cref="SystemHttpClient"/>
	/// so standalone .NET applications work with no setup. Unity applications should override <see cref="Instance"/>
	/// at startup (e.g. via <c>ImpunityUnityHttpClient.Setup(runner)</c>) so requests run through <c>UnityWebRequest</c>.
	/// </summary>
	public static class ImpunityHttp
	{
		/// <summary>The active HTTP client implementation. Defaults to a shared <see cref="SystemHttpClient"/>.</summary>
		public static IImpunityHttpClient Instance = new SystemHttpClient();
	}
}
