#nullable enable

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Impunity.StandaloneServer;

/// <summary>
/// P/Invoke for the part of libdatachannel's C API (rtc.h) the WebRTC transport uses, and the code that finds and
/// loads the library. Written against exactly one release, <see cref="ExpectedTag"/>: rtcConfiguration changes
/// between releases, so <see cref="TryLoad"/> refuses a library whose manifest names another one. The libraries and
/// manifests come from native/libdatachannel/build.sh.
/// </summary>
internal static class LibDataChannelNative
{
	/// <summary>The libdatachannel release these declarations match. Keep in step with native/libdatachannel/build.sh.</summary>
	public const string ExpectedTag = "v0.24.6";

	const string Lib = "datachannel";

	// rtcState
	public const int StateConnecting = 1;
	public const int StateConnected = 2;
	public const int StateDisconnected = 3;
	public const int StateFailed = 4;
	public const int StateClosed = 5;

	// rtcGatheringState
	public const int GatheringComplete = 2;

	// rtcLogLevel
	public const int LogWarning = 3;

	/// <summary>rtcConfiguration of v0.24.6. C <c>bool</c> is one byte.</summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct Configuration
	{
		public IntPtr IceServers;
		public int IceServersCount;
		public IntPtr ProxyServer;
		public IntPtr BindAddress;
		public int CertificateType;
		public int IceTransportPolicy;
		public byte EnableIceTcp;
		public byte EnableIceUdpMux;
		public byte DisableAutoNegotiation;
		public byte ForceMediaTransport;
		public ushort PortRangeBegin;
		public ushort PortRangeEnd;
		public int Mtu;
		public int MaxMessageSize;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct Reliability
	{
		public byte Unordered;
		public byte Unreliable;
		public uint MaxPacketLifeTime;
		public uint MaxRetransmits;
	}

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void LogCallback(int level, IntPtr message);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void StateCallback(int pc, int state, IntPtr userPointer);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void DataChannelCallback(int pc, int dc, IntPtr userPointer);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void ChannelCallback(int id, IntPtr userPointer);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void ErrorCallback(int id, IntPtr error, IntPtr userPointer);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void MessageCallback(int id, IntPtr message, int size, IntPtr userPointer);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void rtcInitLogger(int level, LogCallback? callback);
	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcCreatePeerConnection(ref Configuration config);
	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcDeletePeerConnection(int pc);
	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSetStateChangeCallback(int pc, StateCallback? callback);
	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSetGatheringStateChangeCallback(int pc, StateCallback? callback);
	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSetDataChannelCallback(int pc, DataChannelCallback? callback);
	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern int rtcSetRemoteDescription(int pc, [MarshalAs(UnmanagedType.LPUTF8Str)] string sdp, [MarshalAs(UnmanagedType.LPUTF8Str)] string type);
	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcGetLocalDescription(int pc, byte[] buffer, int size);
	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSetOpenCallback(int id, ChannelCallback? callback);
	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSetClosedCallback(int id, ChannelCallback? callback);
	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSetErrorCallback(int id, ErrorCallback? callback);
	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSetMessageCallback(int id, MessageCallback? callback);
	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSendMessage(int id, ref byte data, int size);
	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcGetBufferedAmount(int id);
	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcGetDataChannelReliability(int dc, out Reliability reliability);
	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcDeleteDataChannel(int dc);
	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void rtcCleanup();

	static IntPtr Handle;

	/// <summary>The runtime id of the library this process would load, or null on a platform none is built for. Goes
	/// by the process architecture, which is what matters under emulation.</summary>
	public static (string rid, string file)? PlatformLibrary()
	{
		Architecture arch = RuntimeInformation.ProcessArchitecture;
		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && arch == Architecture.Arm64)
		{
			return ("osx-arm64", "libdatachannel.dylib");
		}
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && arch == Architecture.X64)
		{
			return ("linux-x64", "libdatachannel.so");
		}
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && arch == Architecture.Arm64)
		{
			return ("linux-arm64", "libdatachannel.so");
		}
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && arch == Architecture.X64)
		{
			return ("win-x64", "datachannel.dll");
		}
		return null;
	}

	/// <summary>
	/// Finds this platform's library under <paramref name="runtimesDirectory"/>, checks it against its manifest and
	/// loads it. Never throws: on any failure returns false with a sentence for the log in <paramref name="problem"/>,
	/// and the server carries on without WebRTC.
	/// </summary>
	public static bool TryLoad(string runtimesDirectory, out string problem)
	{
		problem = "";
		if (Handle != IntPtr.Zero)
		{
			return true;
		}

		try
		{
			(string rid, string file)? platform = PlatformLibrary();
			if (platform == null)
			{
				problem = "no libdatachannel build for " + RuntimeInformation.OSDescription + " " + RuntimeInformation.ProcessArchitecture;
				return false;
			}

			string directory = Path.Combine(runtimesDirectory, platform.Value.rid, "native");
			string path = Path.Combine(directory, platform.Value.file);
			string manifestPath = Path.Combine(directory, "libdatachannel.manifest.json");
			if (!File.Exists(path) || !File.Exists(manifestPath))
			{
				problem = "library or manifest missing under " + directory;
				return false;
			}

			using (JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath)))
			{
				string? tag = manifest.RootElement.GetProperty("libdatachannel").GetProperty("tag").GetString();
				if (tag != ExpectedTag)
				{
					problem = "library is libdatachannel " + tag + ", this server is written for " + ExpectedTag;
					return false;
				}

				string? expectedHash = manifest.RootElement.GetProperty("sha256").GetString();
				string actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
				if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
				{
					problem = "library at " + path + " does not match its manifest (rebuild it with native/libdatachannel/build.sh)";
					return false;
				}
			}

			if (!NativeLibrary.TryLoad(path, out IntPtr handle))
			{
				problem = "could not load " + path;
				if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
				{
					problem += " (if macOS quarantined it, run: xattr -dr com.apple.quarantine \"" + runtimesDirectory + "\")";
				}
				else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
				{
					problem += " (needs glibc 2.34 or later; musl is not supported)";
				}
				return false;
			}

			Handle = handle;
			NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), Resolve);
			return true;
		}
		catch (Exception e)
		{
			problem = e.GetType().Name + ": " + e.Message;
			return false;
		}
	}

	static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
	{
		return name == Lib ? Handle : IntPtr.Zero;
	}
}
