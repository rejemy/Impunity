#nullable enable

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Impunity.StandaloneServer;

/// <summary>
/// The server's WebRTC capability: loads libdatachannel once, owns the settings and the peer limit, creates a
/// <see cref="WebRtcSession"/> per WebSocket that asks for one, and routes native callbacks to them. Everything that
/// can go wrong here only ever turns WebRTC off (<see cref="Available"/> false); the rest of the server is unaffected.
/// See docs/designs/WebRtcTransport.md.
/// </summary>
public class WebRtcService
{
	/// <summary>How often sessions are checked for silence and sent a keepalive.</summary>
	const int TickMillis = 500;

	readonly WebRtcConfig Config;
	readonly string RuntimesDirectory;

	// Sessions by native peer id and by native data channel id, for routing callbacks.
	readonly ConcurrentDictionary<int, WebRtcSession> ByPeer = new();
	readonly ConcurrentDictionary<int, WebRtcSession> ByChannel = new();

	Timer? Ticker;

	// Native code holds these for the life of the process; fields keep the delegates from being collected.
	readonly LibDataChannelNative.LogCallback LogHandler;
	readonly LibDataChannelNative.StateCallback StateHandler;
	readonly LibDataChannelNative.StateCallback GatheringHandler;
	readonly LibDataChannelNative.DataChannelCallback DataChannelHandler;
	readonly LibDataChannelNative.ChannelCallback OpenHandler;
	readonly LibDataChannelNative.ChannelCallback ClosedHandler;
	readonly LibDataChannelNative.ErrorCallback ErrorHandler;
	readonly LibDataChannelNative.MessageCallback MessageHandler;

	/// <summary>Whether offers can be answered.</summary>
	public bool Available { get; private set; }

	/// <summary>Why not, in a word a client may be told: "disabled" or "unavailable".</summary>
	public string UnavailableReason { get; private set; } = "unavailable";

	/// <summary>Address to advertise beside the ones the server finds on its own interfaces, or null.</summary>
	public IPAddress? PublicAddress { get; private set; }

	public ushort UdpPort => Config.udp_port;

	public int PeerCount => ByPeer.Count;

	public WebRtcService(WebRtcConfig? config, string? runtimesDirectory = null)
	{
		Config = config ?? new WebRtcConfig();
		// IMPUNITY_WEBRTC_RUNTIMES points the loader somewhere else; tests use it to stand in for a missing library.
		RuntimesDirectory = runtimesDirectory
			?? Environment.GetEnvironmentVariable("IMPUNITY_WEBRTC_RUNTIMES")
			?? Path.Combine(AppContext.BaseDirectory, "runtimes");

		LogHandler = OnLog;
		StateHandler = OnState;
		GatheringHandler = OnGatheringState;
		DataChannelHandler = OnDataChannel;
		OpenHandler = OnOpen;
		ClosedHandler = OnClosed;
		ErrorHandler = OnError;
		MessageHandler = OnMessage;
	}

	/// <summary>Brings WebRTC up if it can be. Never throws; logs one line saying which way it went.</summary>
	public void Start()
	{
		if (!Config.enabled)
		{
			UnavailableReason = "disabled";
			ImpunityLogger.LogInformation("WebRTC disabled by configuration");
			return;
		}

		try
		{
			if (!LibDataChannelNative.TryLoad(RuntimesDirectory, out string problem))
			{
				ImpunityLogger.LogWarning("WebRTC unavailable, clients will use WebSocket only: " + problem);
				return;
			}

			if (!UdpPortIsFree(out string portProblem))
			{
				ImpunityLogger.LogWarning("WebRTC unavailable, clients will use WebSocket only: UDP port " + Config.udp_port + " can't be bound (" + portProblem + ")");
				return;
			}

			ResolvePublicAddress();

			LibDataChannelNative.rtcInitLogger(LibDataChannelNative.LogWarning, LogHandler);

			// Loading proves little; make and drop one peer to exercise the library for real.
			LibDataChannelNative.Configuration config = default;
			int pc = LibDataChannelNative.rtcCreatePeerConnection(ref config);
			if (pc < 0)
			{
				ImpunityLogger.LogWarning("WebRTC unavailable, clients will use WebSocket only: libdatachannel could not create a peer connection (" + pc + ")");
				return;
			}
			LibDataChannelNative.rtcDeletePeerConnection(pc);

			Ticker = new Timer(Tick, null, TickMillis, TickMillis);
			Available = true;
			ImpunityLogger.LogInformation("WebRTC available on UDP port " + Config.udp_port
				+ (PublicAddress != null ? ", advertising " + PublicAddress : "")
				+ " (libdatachannel " + LibDataChannelNative.ExpectedTag + ", up to " + Config.max_peers + " peers)");
		}
		catch (Exception e)
		{
			Available = false;
			ImpunityLogger.LogWarning("WebRTC unavailable, clients will use WebSocket only: " + e.GetType().Name + ": " + e.Message);
		}
	}

	// libjuice only binds the shared port when the first peer gathers candidates, which is too late to find out
	// that something else has it.
	bool UdpPortIsFree(out string problem)
	{
		problem = "";
		try
		{
			IPAddress address = IPAddress.Any;
			if (!string.IsNullOrEmpty(Config.bind_address) && !IPAddress.TryParse(Config.bind_address, out address!))
			{
				problem = "bind_address '" + Config.bind_address + "' is not an IP address";
				return false;
			}
			using var probe = new UdpClient(new IPEndPoint(address, Config.udp_port));
			return true;
		}
		catch (SocketException e)
		{
			problem = e.SocketErrorCode.ToString();
			return false;
		}
	}

	void ResolvePublicAddress()
	{
		if (string.IsNullOrEmpty(Config.public_address))
		{
			return;
		}

		try
		{
			if (!IPAddress.TryParse(Config.public_address, out IPAddress? address))
			{
				IPAddress[] found = Dns.GetHostAddresses(Config.public_address);
				address = found.Length > 0 ? found[0] : null;
			}
			PublicAddress = address;
		}
		catch (Exception e)
		{
			ImpunityLogger.LogWarning("WebRTC public_address '" + Config.public_address + "' could not be resolved and is ignored: " + e.Message);
		}
	}

	/// <summary>Makes a session for one WebSocket, or returns null with the reason to tell the client
	/// ("disabled", "unavailable" or "full").</summary>
	public WebRtcSession? CreateSession(out string reason)
	{
		reason = UnavailableReason;
		if (!Available)
		{
			return null;
		}

		if (ByPeer.Count >= Config.max_peers)
		{
			reason = "full";
			return null;
		}

		LibDataChannelNative.Configuration config = default;
		config.EnableIceUdpMux = 1;
		config.PortRangeBegin = Config.udp_port;
		config.PortRangeEnd = Config.udp_port;

		IntPtr bindAddress = IntPtr.Zero;
		try
		{
			if (!string.IsNullOrEmpty(Config.bind_address))
			{
				bindAddress = Marshal.StringToCoTaskMemUTF8(Config.bind_address);
				config.BindAddress = bindAddress;
			}

			int pc = LibDataChannelNative.rtcCreatePeerConnection(ref config);
			if (pc < 0)
			{
				ImpunityLogger.LogWarning("WebRTC: could not create a peer connection (" + pc + ")");
				reason = "unavailable";
				return null;
			}

			var session = new WebRtcSession(this, pc);
			ByPeer[pc] = session;
			LibDataChannelNative.rtcSetStateChangeCallback(pc, StateHandler);
			LibDataChannelNative.rtcSetGatheringStateChangeCallback(pc, GatheringHandler);
			LibDataChannelNative.rtcSetDataChannelCallback(pc, DataChannelHandler);
			return session;
		}
		catch (Exception e)
		{
			ImpunityLogger.LogWarning("WebRTC: could not create a peer connection: " + e.Message);
			reason = "unavailable";
			return null;
		}
		finally
		{
			if (bindAddress != IntPtr.Zero)
			{
				Marshal.FreeCoTaskMem(bindAddress);
			}
		}
	}

	internal void Forget(WebRtcSession session)
	{
		ByPeer.TryRemove(session.Peer, out _);
		int channel = session.Channel;
		if (channel >= 0)
		{
			ByChannel.TryRemove(channel, out _);
		}
	}

	/// <summary>Closes every session and releases the library's threads. Called once at shutdown.</summary>
	public void Stop()
	{
		if (!Available)
		{
			return;
		}
		Available = false;
		Ticker?.Dispose();

		foreach (WebRtcSession session in ByPeer.Values)
		{
			session.Close("shutdown");
		}

		// rtcCleanup waits for native threads; don't let a stuck one hold up shutdown.
		try
		{
			Task.Run(LibDataChannelNative.rtcCleanup).Wait(TimeSpan.FromSeconds(3));
		}
		catch (Exception)
		{
		}
	}

	void Tick(object? state)
	{
		long now = Environment.TickCount64;
		foreach (WebRtcSession session in ByPeer.Values)
		{
			try
			{
				session.Tick(now);
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("WebRTC session tick failed", e);
			}
		}
	}

	// ----- Native callbacks. They run on libdatachannel's threads, must never throw into native code, and must not
	// delete the object they were called for (WebRtcSession.Close hands that to the thread pool).

	static void OnLog(int level, IntPtr message)
	{
		try
		{
			string text = "libdatachannel: " + Marshal.PtrToStringUTF8(message);
			if (level <= 2)
			{
				ImpunityLogger.LogError(text);
			}
			else
			{
				ImpunityLogger.LogDebug(text);
			}
		}
		catch (Exception)
		{
		}
	}

	void OnState(int pc, int state, IntPtr userPointer)
	{
		try
		{
			if (ByPeer.TryGetValue(pc, out WebRtcSession? session)
				&& (state == LibDataChannelNative.StateFailed || state == LibDataChannelNative.StateClosed || state == LibDataChannelNative.StateDisconnected))
			{
				session.Close("peer " + (state == LibDataChannelNative.StateFailed ? "failed" : state == LibDataChannelNative.StateClosed ? "closed" : "disconnected"));
			}
		}
		catch (Exception e)
		{
			ImpunityLogger.LogError("WebRTC state callback failed", e);
		}
	}

	void OnGatheringState(int pc, int state, IntPtr userPointer)
	{
		try
		{
			if (state == LibDataChannelNative.GatheringComplete && ByPeer.TryGetValue(pc, out WebRtcSession? session))
			{
				session.GatheringComplete();
			}
		}
		catch (Exception e)
		{
			ImpunityLogger.LogError("WebRTC gathering callback failed", e);
		}
	}

	void OnDataChannel(int pc, int dc, IntPtr userPointer)
	{
		try
		{
			if (!ByPeer.TryGetValue(pc, out WebRtcSession? session) || !session.AdoptChannel(dc))
			{
				// A second channel, or one for a session already gone: not ours to keep.
				Task.Run(() => LibDataChannelNative.rtcDeleteDataChannel(dc));
				return;
			}

			ByChannel[dc] = session;
			LibDataChannelNative.rtcSetOpenCallback(dc, OpenHandler);
			LibDataChannelNative.rtcSetClosedCallback(dc, ClosedHandler);
			LibDataChannelNative.rtcSetErrorCallback(dc, ErrorHandler);
			LibDataChannelNative.rtcSetMessageCallback(dc, MessageHandler);
		}
		catch (Exception e)
		{
			ImpunityLogger.LogError("WebRTC data channel callback failed", e);
		}
	}

	void OnOpen(int dc, IntPtr userPointer)
	{
		try
		{
			if (ByChannel.TryGetValue(dc, out WebRtcSession? session))
			{
				session.ChannelOpened();
			}
		}
		catch (Exception e)
		{
			ImpunityLogger.LogError("WebRTC open callback failed", e);
		}
	}

	void OnClosed(int dc, IntPtr userPointer)
	{
		try
		{
			if (ByChannel.TryGetValue(dc, out WebRtcSession? session))
			{
				session.Close("channel closed");
			}
		}
		catch (Exception e)
		{
			ImpunityLogger.LogError("WebRTC closed callback failed", e);
		}
	}

	void OnError(int dc, IntPtr error, IntPtr userPointer)
	{
		try
		{
			if (ByChannel.TryGetValue(dc, out WebRtcSession? session))
			{
				session.Close("channel error: " + Marshal.PtrToStringUTF8(error));
			}
		}
		catch (Exception e)
		{
			ImpunityLogger.LogError("WebRTC error callback failed", e);
		}
	}

	void OnMessage(int dc, IntPtr message, int size, IntPtr userPointer)
	{
		try
		{
			// A negative size is a text message; Impunity frames are binary.
			if (size <= 0 || !ByChannel.TryGetValue(dc, out WebRtcSession? session))
			{
				return;
			}

			// The native buffer is only valid during this call.
			byte[] copy = new byte[size];
			Marshal.Copy(message, copy, 0, size);
			session.MessageReceived(copy);
		}
		catch (Exception e)
		{
			ImpunityLogger.LogError("WebRTC message callback failed", e);
		}
	}
}
