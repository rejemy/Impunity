#nullable disable

using System;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Impunity.Tests
{
	// The complete transport battery over WSS alone and WSS + WebRTC, with a private per-fixture CA.
	[TestFixture(false), TestFixture(true), Category("Https"), Category("Standalone")]
	public class TransportSuite_StandaloneTls : TransportSuite_StandaloneRtcBase
	{
		private readonly bool Rtc;
		public TransportSuite_StandaloneTls(bool rtc) { Rtc = rtc; }
		protected override bool UseHttps => true;
		protected override bool ExpectChannel => Rtc;
		protected override bool NegotiateRtc => Rtc;

		[Test]
		public async Task Renewal_KeepsConnectionsAndReplicationAlive()
		{
			var (a, b, at, bt) = await TwoClients();
			a.Status.Set("before renewal");
			await PumpUntil(() => b.Status.Get() == "before renewal", TimeSpan.FromSeconds(3), AllConnections());
			string old = Certificates.Thumbprint;
			Certificates.Rotate(rsa: true);
			await PumpUntil(() => Server.CapturedOutput().Contains("Loaded HTTPS certificate " + Certificates.Thumbprint),
				TimeSpan.FromSeconds(8), AllConnections());
			Assert.AreNotEqual(old, Certificates.Thumbprint);
			Assert.AreEqual(Certificates.Thumbprint, await Https_Standalone.ReadFingerprint(Server, Certificates));
			a.Status.Set("after renewal");
			await PumpUntil(() => b.Status.Get() == "after renewal", TimeSpan.FromSeconds(3), AllConnections());
			if (Rtc)
			{
				Assert.IsTrue(at.SupportsUnguaranteed && bt.SupportsUnguaranteed, "renewal dropped a data channel");
				a.Status.SetUnguaranteed("rtc after renewal");
				await PumpUntil(() => b.Status.Get() == "rtc after renewal", TimeSpan.FromSeconds(3), AllConnections());
				Assert.Greater(bt.ChannelReceived, 0);
			}
			// A newly connected client must also establish successfully after rotation.
			var fresh = await OpenConnectionAsync();
			Assert.IsTrue(fresh.Connected);
		}

		[Test]
		public async Task DatagramDeliveryAndFallback_OverSecureSocket()
		{
			var (a, b, at, bt) = await TwoClients();
			a.Status.SetUnguaranteed("first");
			await PumpUntil(() => b.Status.Get() == "first", TimeSpan.FromSeconds(3), AllConnections());
			Assert.AreEqual(Rtc ? 1 : 0, bt.ChannelReceived);
			if (Rtc) bt.ClosePeer("test fallback");
			int socketBefore = bt.SocketEntityUpdatesReceived;
			for (int i = 0; i < 50 && b.Status.Get() != "fallback"; i++)
			{
				a.Status.SetUnguaranteed("fallback");
				await PumpFor(TimeSpan.FromMilliseconds(100), AllConnections());
			}
			Assert.AreEqual("fallback", b.Status.Get());
			Assert.Greater(bt.SocketEntityUpdatesReceived, socketBefore);
		}
	}
}
