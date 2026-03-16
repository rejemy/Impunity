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

public class ServerConfig
{
	public string? game_type_code { get; set; }
	public ushort http_port { get; set; } = 29653;
	public ushort tcp_port { get; set; } = 29654;

	public string datapath { get; set; } = "worlds";

	public string? logging_level { get; set; }

	public string? server_hostname { get; set; }

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