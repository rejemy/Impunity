#nullable enable

using System;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;


namespace Impunity.StandaloneServer;

// allow lowercase json naming
#pragma warning disable IDE1006

public class WorldConfig
{
	public string? id { get; set; }
	public string? name { get; set; }
	public string? password { get; set; }
	public int max_players { get; set; } = 16;
}

// Settings for the optional WebRTC transport (docs/designs/WebRtcTransport.md). Leaving the whole object out of
// config.json means enabled with these defaults.
public class WebRtcConfig
{
	// false skips loading the native library altogether.
	public bool enabled { get; set; } = true;

	// The one UDP port every WebRTC client talks to. Must be reachable from clients, like http_port.
	public ushort udp_port { get; set; } = 29656;

	// Local interface to bind, as an IP address. Null binds them all.
	public string? bind_address { get; set; }

	// Address (or host name) clients should send to when it isn't one of this machine's own, i.e. behind NAT:
	// most cloud hosts and Docker.
	public string? public_address { get; set; }

	// Offers beyond this many live peers are refused; those clients stay on WebSocket.
	public int max_peers { get; set; } = 256;
}

public class ServerConfig
{
	public string? game_type_code { get; set; }
	public ushort http_port { get; set; } = 29653;
	public ushort tcp_port { get; set; } = 29654;

	public string datapath { get; set; } = "worlds";

	public string? logging_level { get; set; }

	public string? server_hostname { get; set; }

	// How long a connection may be silent before it's dropped (ImpunityOptions.ConnectionTimeoutMillis). Null keeps the
	// library default; values below the minimum are raised to it; 0 disables keepalives.
	public int? connection_timeout_ms { get; set; }

	// Most outbound bytes held for one client that isn't reading before it's dropped
	// (ImpunityOptions.MaxSendBacklogBytes). Null keeps the library default; 0 means no limit.
	public long? max_send_backlog_bytes { get; set; }

	public WebRtcConfig? webrtc { get; set; }

	public List<WorldConfig>? worlds { get; set; }


	public static ServerConfig? ReadConfig(string path)
	{
		try
		{
			using FileStream stream = File.OpenRead(path);
			return JsonSerializer.Deserialize<ServerConfig>(stream);
		}
		catch (Exception e)
		{
			Console.WriteLine("Unable to read server configuration: " + e.Message);
			return null;
		}
	}

	public ImpunityLogLevel GetLoggingLevel()
	{
		if (logging_level == null)
		{
			return ImpunityLogLevel.INFO;
		}

		switch (logging_level.ToLower())
		{
			case "trace":
				return ImpunityLogLevel.TRACE;
			case "debug":
				return ImpunityLogLevel.DEBUG;
			case "info":
				return ImpunityLogLevel.INFO;
			case "warn":
				return ImpunityLogLevel.WARN;
			case "error":
				return ImpunityLogLevel.ERROR;
			case "critical":
				return ImpunityLogLevel.CRITICAL;
		}

		Console.WriteLine("Unknown log level: " + logging_level);
		return ImpunityLogLevel.INFO;
	}
}

#pragma warning restore IDE1006
