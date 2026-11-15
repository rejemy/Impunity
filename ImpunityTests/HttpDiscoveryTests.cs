#nullable disable

using System;
using System.Collections.Generic;
using NUnit.Framework;
using Impunity.Networking;

namespace Impunity.Tests
{
	[Category("Https"), NonParallelizable]
	public class HttpDiscoveryTests
	{
		private sealed class ReplyClient : IImpunityHttpClient
		{
			public string Url;
			public void Get(string url, ImpunityCallback<string> onComplete)
			{
				Url = url;
				onComplete(null, "{\"ImpunityVersion\":\"" + ImpunityConstants.ImpunityVersion + "\",\"GameType\":\"Test\",\"TCPPort\":29654," +
					"\"Worlds\":[{\"WorldId\":\"world\",\"WorldName\":\"Test world\"}]}");
			}
		}

		[TestCase("https://game.example.com", "wss://game.example.com/ws")]
		[TestCase("https://game.example.com:8443", "wss://game.example.com:8443/ws")]
		[TestCase("http://[::1]:29653", "ws://[::1]:29653/ws")]
		public void Discovery_PreservesSchemeHostAndPort(string origin, string expectedSocket)
		{
			var previous = ImpunityHttp.Instance;
			var client = new ReplyClient();
			ImpunityHttp.Instance = client;
			try
			{
				List<ServerInfo> result = null;
				ImpunityErrorResponse error = null;
				ImpunityHTTPServerFinder.GetServerWorlds(new ImpunityOptions { GameTypeCode = "Test" }, origin,
					(err, worlds) => { error = err; result = worlds; });
				Assert.IsNull(error, error?.Message);
				Assert.AreEqual(origin + "/worlds", client.Url);
				Assert.AreEqual(expectedSocket, result[0].WebSocketUrl);
				Assert.AreEqual(new Uri(origin).Host, result[0].Hostname);
				Assert.AreEqual(29654, result[0].Port, "discovery must retain the independent TCP port");
			}
			finally { ImpunityHttp.Instance = previous; }
		}

		[TestCase("http://example.com", "http://example.com/worlds")]
		[TestCase("https://example.com:8080", "https://example.com:8080/worlds")]
		[TestCase("https://[::1]", "https://[::1]/worlds")]
		public void WorldStatus_UsesTheSuppliedUrl(string serverUrl, string expectedUrl)
		{
			var previous = ImpunityHttp.Instance;
			var client = new ReplyClient();
			ImpunityHttp.Instance = client;
			try
			{
				ImpunityErrorResponse error = null;
				ServerInfo result = null;
				ImpunityHTTPServerFinder.GetServerWorldStatus(new ImpunityOptions { GameTypeCode = "Test" }, serverUrl, "world",
					(err, world) => { error = err; result = world; });
				Assert.IsNull(error, error?.Message);
				Assert.AreEqual("world", result.GameId);
				Assert.AreEqual(expectedUrl, client.Url);
			}
			finally { ImpunityHttp.Instance = previous; }
		}
	}
}
