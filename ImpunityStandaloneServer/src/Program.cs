#nullable enable

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Impunity;
using Impunity.StandaloneServer;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Hosting;


// --------------- Read config file section ---------------------
string configPath = "config.json";

ServerConfig? config = ServerConfig.ReadConfig(configPath);
if (config == null)
{
	Console.WriteLine("Unable to start due to missing configuration file at " + configPath);
	Environment.Exit(-1);
}

if (string.IsNullOrEmpty(config.game_type_code))
{
	Console.WriteLine("Must set a game_type_code in server config");
	Environment.Exit(-1);
}

if (config.worlds == null || config.worlds.Count == 0)
{
	Console.WriteLine("No worlds defined, exiting");
	Environment.Exit(-1);
}

ImpunityLogLevel logLevel = config.GetLoggingLevel();

// --------------- Setup logger for startup process ---------------------


LogLevel aspLogLevel = ImpunityAspLogger.GetAspLogLevel(logLevel);

using var loggerFactory = LoggerFactory.Create(loggingBuilder =>
{
	loggingBuilder.SetMinimumLevel(aspLogLevel);
	loggingBuilder.AddConsole(options =>
	{
		options.FormatterName = "consoleformat";
	});
	loggingBuilder.AddConsoleFormatter<ImpunityConsoleFormatter, ImpunityConsoleFormatterOptions>(options => { });
});

ILogger serverLogger = loggerFactory.CreateLogger("ImpunityServer");
ILogger logger = loggerFactory.CreateLogger("Impunity");
ImpunityAspLogger.Setup(logger, logLevel);

serverLogger.LogInformation("Read configuration from " + configPath);

HttpsCertificateService? certificates = null;
try
{
	config.ValidateWebConfiguration();
	if (config.https?.enabled == true)
		certificates = new HttpsCertificateService(config.https, configPath, serverLogger);
}
catch (Exception e)
{
	serverLogger.LogError("Unable to configure web listeners: {Reason}", e.Message);
	return 1;
}
await using var certificateLifetime = certificates;

// --------------- Create app builder ---------------------

WebApplicationBuilder builder = WebApplication.CreateBuilder();
builder.WebHost.ConfigureKestrel(options =>
{
	if (config.http_enabled) options.ListenAnyIP(config.http_port);
	if (certificates != null)
		options.ListenAnyIP(config.https!.port, listener => listener.UseHttps(new TlsHandshakeCallbackOptions
		{
			OnConnection = _ => ValueTask.FromResult(certificates.CreateAuthenticationOptions())
		}));
});

// --------------- Setup AspNetCore logger ---------------------

builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(aspLogLevel);
builder.Logging.AddConsole(options =>
{
	options.FormatterName = "consoleformat";
});
builder.Logging.AddConsoleFormatter<ImpunityConsoleFormatter, ImpunityConsoleFormatterOptions>(options => { });
builder.Logging.AddFilter((provider, category, logLevel) =>
{
	if (category == null)
	{
		return true;
	}

	if (category.StartsWith("Microsoft") && logLevel <= LogLevel.Information)
	{
		return false;
	}

	return true;
});

// --------------- Create Impunity services ---------------------

ImpunityOptions impOptions = new()
{
	DBPassword = null,
	RemoteUpgradeAllowed = true,
	ServerPort = config.tcp_port,
	GameTypeCode = config.game_type_code
};
if (config.connection_timeout_ms.HasValue)
{
	impOptions.ConnectionTimeoutMillis = config.connection_timeout_ms.Value;
}
if (config.max_send_backlog_bytes.HasValue)
{
	impOptions.MaxSendBacklogBytes = config.max_send_backlog_bytes.Value;
}

WorldService worldService = new WorldService(config.datapath, config.worlds, impOptions);
ConnectionService? connectionService = null;
WebRtcService? webRtcService = null;
try
{
	builder.Services.AddSingleton(worldService);

	connectionService = new ConnectionService(worldService, impOptions);
	builder.Services.AddSingleton(connectionService);


	builder.Services.AddSingleton(new InfoService(impOptions));

	// Optional: anything that goes wrong in here only leaves clients on WebSocket.
	webRtcService = new WebRtcService(config.webrtc);
	builder.Services.AddSingleton(webRtcService);

	// --------------- Setup AspNetCore web server ---------------------
	builder.Services.AddSingleton<IHostLifetime, ConsoleLifetime>();

	builder.Services.AddControllers().AddJsonOptions(options =>
		{
			options.JsonSerializerOptions.WriteIndented = true;
		});

	builder.Environment.ApplicationName = "ImpunityStandaloneServer";

	string[]? origins = config.allowed_origins ?? (config.server_hostname == null ? null
		: new[] { "http://" + config.server_hostname, "https://" + config.server_hostname });
	if (origins != null)
	{
		builder.Services.AddCors(options =>
		{
			options.AddDefaultPolicy(
				policy =>
				{
					policy.WithOrigins(origins).WithMethods("GET").AllowAnyHeader();
				});
		});
	}

	await using WebApplication app = builder.Build();

	WebSocketOptions webSocketOptions = new()
	{
		KeepAliveInterval = TimeSpan.FromMinutes(1)
	};

	app.UseRouting();
	if (origins != null)
	{
		app.UseCors();
	}

	if (config.allowed_origins != null)
		foreach (string origin in config.allowed_origins) webSocketOptions.AllowedOrigins.Add(origin);
	app.UseWebSockets(webSocketOptions);

	app.MapControllers();

	app.Lifetime.ApplicationStarted.Register(() =>
	{
		if (config.http_enabled) serverLogger.LogInformation("Listening for HTTP on port {port}", config.http_port);
		if (certificates != null) serverLogger.LogInformation("Listening for HTTPS/WSS on port {port}", config.https!.port);
	});

	serverLogger.LogInformation("Impunity Standalone Server starting up");

	// --------------- Run server ---------------------

	connectionService.Start();
	serverLogger.LogInformation("Listening for TCP on port {port}", impOptions.ServerPort);
	webRtcService.Start();
	certificates?.Start();
	await app.RunAsync();
}
finally
{

	// --------------- Shutdown server ---------------------

	serverLogger.LogInformation("Shutting down");

	webRtcService?.Stop();

	connectionService?.Stop();

	worldService.Stop();

	serverLogger.LogInformation("Impunity Standalone Server done");
}
return 0;
