#nullable enable

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Impunity.StandaloneServer;

/// <summary>
/// One WebRTC peer connection and its single unordered, unreliable data channel, belonging to one WebSocket. The
/// browser offers and creates the channel; this side answers. Messages on the channel are whole Impunity frames.
/// A session is used once: after <see cref="Close"/> the client must offer again and gets a new one.
/// </summary>
public class WebRtcSession
{
	/// <summary>Largest offer accepted, in characters.</summary>
	public const int MaxOfferLength = 16 * 1024;

	// A send is skipped when this much is already waiting in the channel: old movement is worth less than none.
	const int SendBudgetBytes = 16 * 1024;
	// With nothing else to send for this long, send a keepalive so the other end can tell the path still works.
	const long KeepAliveAfterMillis = 1000;
	// Nothing received for this long on an open channel: the path is dead, go back to the WebSocket.
	const long SilenceLimitMillis = 5000;
	// An offer that hasn't produced an open channel by now never will.
	const long NegotiationLimitMillis = 15000;
	const int GatheringTimeoutMillis = 5000;

	// The server's header-only KEEPALIVE frame: length 12, type ServerActionType.KEEPALIVE (2), id 0, flags 0.
	static readonly byte[] KeepAliveFrame = { 12, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0 };

	readonly WebRtcService Service;
	readonly TaskCompletionSource<bool> Gathered = new(TaskCreationOptions.RunContinuationsAsynchronously);
	readonly long CreatedAt = Environment.TickCount64;

	int ChannelId = -1;
	int ClosedFlag;
	volatile bool Open;
	long LastSent;
	long LastReceived;

	internal int Peer { get; }
	internal int Channel => Volatile.Read(ref ChannelId);

	/// <summary>Whether the data channel is open, i.e. unguaranteed messages can be sent on it right now.</summary>
	public bool IsOpen => Open;

	/// <summary>One complete Impunity frame arrived on the data channel. Called on a libdatachannel thread.</summary>
	public Action<ArraySegment<byte>>? OnMessage;

	/// <summary>The session ended (once, with a reason for the log). Called on whichever thread noticed.</summary>
	public Action<WebRtcSession, string>? OnClosed;

	internal WebRtcSession(WebRtcService service, int peer)
	{
		Service = service;
		Peer = peer;
	}

	/// <summary>
	/// Applies the browser's offer and returns the answer, complete with this server's candidates. Throws if the
	/// offer is unusable; the caller then closes the session.
	/// </summary>
	public async Task<string> AnswerAsync(string offerSdp)
	{
		if (offerSdp.Length > MaxOfferLength)
		{
			throw new ArgumentException("offer too large");
		}

		// With auto-negotiation on (the default), setting the remote offer makes libdatachannel produce the answer
		// and start gathering.
		int result = LibDataChannelNative.rtcSetRemoteDescription(Peer, WithoutCandidates(offerSdp), "offer");
		if (result < 0)
		{
			throw new ArgumentException("offer rejected (" + result + ")");
		}

		Task finished = await Task.WhenAny(Gathered.Task, Task.Delay(GatheringTimeoutMillis));
		if (finished != Gathered.Task)
		{
			throw new TimeoutException("candidate gathering did not finish");
		}

		byte[] buffer = new byte[MaxOfferLength];
		int length = LibDataChannelNative.rtcGetLocalDescription(Peer, buffer, buffer.Length);
		if (length <= 1)
		{
			throw new InvalidOperationException("no local description (" + length + ")");
		}

		// The returned length counts the terminating zero.
		string answer = Encoding.UTF8.GetString(buffer, 0, length - 1);
		if (!answer.Contains("a=candidate"))
		{
			throw new InvalidOperationException("no local candidates (is UDP port " + Service.UdpPort + " free?)");
		}
		return WithPublicCandidate(answer);
	}

	// The server never sends to an address a client merely names: it learns the client's address from the client's
	// own first connectivity check. Browser host candidates are mDNS names it couldn't resolve anyway.
	static string WithoutCandidates(string sdp)
	{
		var kept = new StringBuilder(sdp.Length);
		foreach (string rawLine in sdp.Split('\n'))
		{
			string line = rawLine.TrimEnd('\r');
			if (line.Length == 0 || line.StartsWith("a=candidate", StringComparison.Ordinal) || line.StartsWith("a=end-of-candidates", StringComparison.Ordinal))
			{
				continue;
			}
			kept.Append(line).Append("\r\n");
		}
		return kept.ToString();
	}

	// Behind one-to-one NAT (most cloud hosts, Docker) the server only sees its private address, which a client
	// can't reach. The configured public address goes in as one more host candidate on the same port. Only the
	// candidate list changes; the fingerprint and ICE credentials are libdatachannel's.
	string WithPublicCandidate(string answer)
	{
		if (Service.PublicAddress == null)
		{
			return answer;
		}

		string candidate = "a=candidate:9 1 UDP 2130706431 " + Service.PublicAddress + " " + Service.UdpPort + " typ host";
		var result = new StringBuilder(answer.Length + candidate.Length + 2);
		bool added = false;
		foreach (string rawLine in answer.Split('\n'))
		{
			string line = rawLine.TrimEnd('\r');
			if (line.Length == 0)
			{
				continue;
			}
			if (!added && line.StartsWith("a=candidate", StringComparison.Ordinal))
			{
				result.Append(candidate).Append("\r\n");
				added = true;
			}
			result.Append(line).Append("\r\n");
		}
		return result.ToString();
	}

	/// <summary>Sends one Impunity frame on the channel, or drops it: when the channel isn't open, when too much is
	/// already queued, or when the send fails. Never blocks and never throws.</summary>
	public void Send(ArraySegment<byte> message)
	{
		int channel = Channel;
		if (!Open || channel < 0 || message.Array == null || message.Count == 0)
		{
			return;
		}

		try
		{
			if (LibDataChannelNative.rtcGetBufferedAmount(channel) > SendBudgetBytes)
			{
				return;
			}

			if (LibDataChannelNative.rtcSendMessage(channel, ref message.Array[message.Offset], message.Count) >= 0)
			{
				Volatile.Write(ref LastSent, Environment.TickCount64);
			}
		}
		catch (Exception e)
		{
			ImpunityLogger.LogDebug("WebRTC send failed: " + e.Message);
		}
	}

	internal void GatheringComplete()
	{
		Gathered.TrySetResult(true);
	}

	// Takes the browser's channel if it is the first and is what the transport needs: no ordering, no retransmits.
	internal bool AdoptChannel(int dc)
	{
		if (Volatile.Read(ref ClosedFlag) != 0 || Interlocked.CompareExchange(ref ChannelId, dc, -1) != -1)
		{
			return false;
		}

		if (LibDataChannelNative.rtcGetDataChannelReliability(dc, out LibDataChannelNative.Reliability reliability) < 0
			|| reliability.Unordered == 0 || reliability.Unreliable == 0 || reliability.MaxRetransmits != 0 || reliability.MaxPacketLifeTime != 0)
		{
			Close("channel is not unordered with zero retransmits");
		}
		return true;
	}

	internal void ChannelOpened()
	{
		long now = Environment.TickCount64;
		Volatile.Write(ref LastSent, now);
		Volatile.Write(ref LastReceived, now);
		if (Volatile.Read(ref ClosedFlag) == 0)
		{
			Open = true;
			ImpunityLogger.LogDebug("WebRTC channel open for peer " + Peer);
		}
	}

	internal void MessageReceived(byte[] message)
	{
		Volatile.Write(ref LastReceived, Environment.TickCount64);
		if (Open)
		{
			OnMessage?.Invoke(new ArraySegment<byte>(message));
		}
	}

	internal void Tick(long now)
	{
		if (!Open)
		{
			if (now - CreatedAt > NegotiationLimitMillis)
			{
				Close("never connected");
			}
			return;
		}

		if (now - Volatile.Read(ref LastReceived) > SilenceLimitMillis)
		{
			Close("silent");
		}
		else if (now - Volatile.Read(ref LastSent) > KeepAliveAfterMillis)
		{
			Send(new ArraySegment<byte>(KeepAliveFrame));
		}
	}

	/// <summary>Ends the session: stops sends at once, tells the owner, and deletes the native objects on the thread
	/// pool (never on the calling thread, which may be one of their own callbacks). Safe to call repeatedly and from
	/// any thread.</summary>
	public void Close(string reason)
	{
		if (Interlocked.Exchange(ref ClosedFlag, 1) != 0)
		{
			return;
		}

		Open = false;
		Gathered.TrySetResult(false);
		int channel = Channel;
		Service.Forget(this);
		ImpunityLogger.LogDebug("WebRTC session for peer " + Peer + " closed: " + reason);

		try
		{
			OnClosed?.Invoke(this, reason);
		}
		catch (Exception e)
		{
			ImpunityLogger.LogError("WebRTC close handler failed", e);
		}

		Task.Run(() =>
		{
			try
			{
				if (channel >= 0)
				{
					LibDataChannelNative.rtcDeleteDataChannel(channel);
				}
				LibDataChannelNative.rtcDeletePeerConnection(Peer);
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("WebRTC peer cleanup failed", e);
			}
		});
	}
}
