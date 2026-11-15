#nullable disable

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Impunity.Tests
{
	[Category("Https"), Category("Standalone")]
	public class Https_Standalone
	{
		internal static async Task<string> ReadFingerprint(StandaloneServerFixture server, TestCertificates certificates, string name = "localhost")
		{
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			using var tcp = new TcpClient();
			await tcp.ConnectAsync("127.0.0.1", server.HttpsPort, timeout.Token);
			using var tls = new SslStream(tcp.GetStream(), false, certificates.Validate);
			await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
			{
				TargetHost = name, AllowTlsResume = false,
				CertificateRevocationCheckMode = X509RevocationMode.NoCheck
			}, timeout.Token);
			return tls.RemoteCertificate.GetCertHashString();
		}

		private static async Task WaitForCertificate(StandaloneServerFixture server, TestCertificates certificates)
		{
			var deadline = System.Diagnostics.Stopwatch.StartNew();
			while (deadline.Elapsed < TimeSpan.FromSeconds(8))
			{
				try
				{
					if (await ReadFingerprint(server, certificates) == certificates.Thumbprint) return;
				}
				catch (AuthenticationException) { }
				await Task.Delay(100);
			}
			Assert.Fail("Certificate was not reloaded.\n" + server.CapturedOutput());
		}

		[TestCase(false), TestCase(true)]
		public async Task Https_SendsIntermediateChain_AndValidatesNames(bool rsa)
		{
			using var certificates = new TestCertificates();
			certificates.Rotate(rsa);
			using var server = StandaloneServerFixture.Launch(certificates: certificates);
			using var http = server.CreateHttpClient();
			StringAssert.Contains("\"status\": \"OK\"", await http.GetStringAsync(server.BaseUrl + "/info"));
			StringAssert.Contains("testworld", await http.GetStringAsync(server.BaseUrl + "/worlds"));
			Assert.AreEqual(certificates.Thumbprint, await ReadFingerprint(server, certificates));
			Assert.ThrowsAsync<AuthenticationException>(async () => await ReadFingerprint(server, certificates, "wrong.example.com"));
			using var unrelated = new TestCertificates();
			Assert.ThrowsAsync<AuthenticationException>(async () => await ReadFingerprint(server, unrelated));
			using var plain = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
			Assert.ThrowsAsync<HttpRequestException>(async () => await plain.GetAsync($"http://127.0.0.1:{server.HttpPort}/info"));
		}

		[Test]
		public async Task HttpAndHttps_CanCoexist_WithExplicitCorsAndWebSocketOrigins()
		{
			using var certificates = new TestCertificates();
			using var server = StandaloneServerFixture.Launch(certificates: certificates, configure: config =>
			{
				config["http_enabled"] = true;
				config["allowed_origins"] = new JsonArray("https://game.example.com:8443");
			});
			using var http = server.CreateHttpClient();
			foreach (string endpoint in new[] { server.BaseUrl, $"http://127.0.0.1:{server.HttpPort}" })
			{
				foreach (string origin in new[] { "https://game.example.com:8443", "https://game.example.com", "null" })
				{
					bool allowed = origin.EndsWith(":8443");
					using var request = new HttpRequestMessage(HttpMethod.Get, endpoint + "/worlds");
					request.Headers.Add("Origin", origin);
					using var response = await http.SendAsync(request);
					Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
					Assert.AreEqual(allowed, response.Headers.Contains("Access-Control-Allow-Origin"));
					using var preflight = new HttpRequestMessage(HttpMethod.Options, endpoint + "/worlds");
					preflight.Headers.Add("Origin", origin);
					preflight.Headers.Add("Access-Control-Request-Method", "GET");
					preflight.Headers.Add("Access-Control-Request-Headers", "x-test");
					using var preflightReply = await http.SendAsync(preflight);
					Assert.AreEqual(allowed, preflightReply.Headers.Contains("Access-Control-Allow-Origin"));
					using var socket = new ClientWebSocket();
					socket.Options.RemoteCertificateValidationCallback = certificates.Validate;
					socket.Options.SetRequestHeader("Origin", origin);
					using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
					var url = new Uri(endpoint.Replace("https://", "wss://").Replace("http://", "ws://") + "/ws");
					if (allowed) await socket.ConnectAsync(url, timeout.Token);
					else Assert.ThrowsAsync<WebSocketException>(async () => await socket.ConnectAsync(url, timeout.Token));
				}
			}
			using var native = new ClientWebSocket();
			native.Options.RemoteCertificateValidationCallback = certificates.Validate;
			using var nativeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			await native.ConnectAsync(new Uri(server.WebSocketUrl), nativeTimeout.Token);
		}

		[Test]
		public async Task Renewal_RetriesInvalidFiles_AndFollowsCertbotSymlinks()
		{
			using var certificates = new TestCertificates();
			using var server = StandaloneServerFixture.Launch(certificates: certificates);
			string oldFingerprint = certificates.Thumbprint;
			string oldKey = File.ReadAllText(certificates.PrivateKeyPath);
			var replacement = certificates.Issue(rsa: true);
			// Separate renewal operations temporarily leave a new certificate next to the previous private key.
			File.WriteAllText(certificates.CertificatePath, replacement.certificate);
			await Task.Delay(1500);
			Assert.AreEqual(oldFingerprint, await ReadFingerprint(server, certificates));
			StringAssert.Contains("HTTPS certificate reload failed", server.CapturedOutput());
			File.Delete(certificates.PrivateKeyPath);
			await Task.Delay(1500);
			Assert.AreEqual(oldFingerprint, await ReadFingerprint(server, certificates));
			File.WriteAllText(certificates.CertificatePath, "incomplete PEM");
			File.WriteAllText(certificates.PrivateKeyPath, oldKey);
			await Task.Delay(1500);
			Assert.AreEqual(oldFingerprint, await ReadFingerprint(server, certificates));
			var expired = certificates.Issue(expired: true);
			File.WriteAllText(certificates.CertificatePath, expired.certificate);
			File.WriteAllText(certificates.PrivateKeyPath, expired.key);
			await Task.Delay(1500);
			Assert.AreEqual(oldFingerprint, await ReadFingerprint(server, certificates));

			// Model live/fullchain.pem and live/privkey.pem pointing into archive/. Change both link targets,
			// while retaining timestamps, so file watchers/mtime-only checks cannot be required for success.
			certificates.Rotate(rsa: true);
			string archive = Path.Combine(certificates.DirectoryPath, "archive");
			Directory.CreateDirectory(archive);
			string archivedCert = Path.Combine(archive, "fullchain2.pem");
			string archivedKey = Path.Combine(archive, "privkey2.pem");
			File.Move(certificates.CertificatePath, archivedCert);
			File.Move(certificates.PrivateKeyPath, archivedKey);
			File.SetLastWriteTimeUtc(archivedCert, DateTime.UtcNow.AddDays(-1));
			File.CreateSymbolicLink(certificates.CertificatePath, archivedCert);
			File.CreateSymbolicLink(certificates.PrivateKeyPath, archivedKey);
			await WaitForCertificate(server, certificates);
			int loaded = server.CapturedOutput().Split("Loaded HTTPS certificate").Length;
			await Task.Delay(2200);
			Assert.AreEqual(loaded, server.CapturedOutput().Split("Loaded HTTPS certificate").Length, "unchanged files should not create generations");

			// A different chain must be sent after rotation too, without a cached intermediate from generation A.
			using var newChain = new TestCertificates();
			File.Copy(newChain.CertificatePath, Path.Combine(archive, "fullchain3.pem"));
			File.Copy(newChain.PrivateKeyPath, Path.Combine(archive, "privkey3.pem"));
			File.CreateSymbolicLink(certificates.CertificatePath + ".next", Path.Combine("archive", "fullchain3.pem"));
			File.CreateSymbolicLink(certificates.PrivateKeyPath + ".next", Path.Combine("archive", "privkey3.pem"));
			File.Move(certificates.CertificatePath + ".next", certificates.CertificatePath, overwrite: true);
			File.Move(certificates.PrivateKeyPath + ".next", certificates.PrivateKeyPath, overwrite: true);
			await WaitForCertificate(server, newChain);
		}

		[Test]
		public async Task Renewal_AllowsConcurrentHandshakes()
		{
			using var certificates = new TestCertificates();
			using var server = StandaloneServerFixture.Launch(certificates: certificates);
			string before = certificates.Thumbprint;
			certificates.Rotate();
			var workers = new Task[8];
			for (int i = 0; i < workers.Length; i++) workers[i] = Probe();
			await Task.WhenAll(workers);
			await WaitForCertificate(server, certificates);

			async Task Probe()
			{
				for (int j = 0; j < 15; j++)
				{
					string observed = await ReadFingerprint(server, certificates);
					Assert.IsTrue(observed == before || observed == certificates.Thumbprint);
					await Task.Delay(100);
				}
			}
		}

		[TestCase("missing"), TestCase("mismatch"), TestCase("expired"), TestCase("usage"), TestCase("chain")]
		public void InvalidInitialCertificate_FailsStartup(string problem)
		{
			using var certificates = new TestCertificates();
			if (problem == "missing") File.Delete(certificates.PrivateKeyPath);
			else if (problem == "mismatch") File.WriteAllText(certificates.PrivateKeyPath, certificates.Issue().key);
			else if (problem == "chain")
			{
				using var leaf = X509Certificate2.CreateFromPem(File.ReadAllText(certificates.CertificatePath));
				File.WriteAllText(certificates.CertificatePath, leaf.ExportCertificatePem());
			}
			else
			{
				var files = certificates.Issue(expired: problem == "expired", clientOnly: problem == "usage");
				File.WriteAllText(certificates.CertificatePath, files.certificate);
				File.WriteAllText(certificates.PrivateKeyPath, files.key);
			}
			var error = Assert.Throws<InvalidOperationException>(() => StandaloneServerFixture.Launch(certificates: certificates));
			StringAssert.Contains("Unable to configure web listeners", error.Message);
			StringAssert.DoesNotContain("Listening for TCP", error.Message);
		}

		[TestCase("listeners"), TestCase("origins"), TestCase("path"), TestCase("interval"), TestCase("port")]
		public void InvalidConfiguration_FailsBeforeStartingServices(string problem)
		{
			using var certificates = new TestCertificates();
			var error = Assert.Throws<InvalidOperationException>(() => StandaloneServerFixture.Launch(certificates: certificates, configure: config =>
			{
				if (problem == "listeners") config["https"]["enabled"] = false;
				if (problem == "origins") config["allowed_origins"] = new JsonArray();
				if (problem == "path") config["allowed_origins"] = new JsonArray("https://game.example.com/path");
				if (problem == "interval") config["https"]["reload_interval_seconds"] = 0;
				if (problem == "port") config["https"]["port"] = config["tcp_port"].GetValue<int>();
			}));
			StringAssert.Contains("Unable to configure web listeners", error.Message);
		}
	}
}
