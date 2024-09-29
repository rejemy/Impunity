#nullable enable

using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Impunity;
using Impunity.StandaloneServer;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Hosting;

// dotnet 6 style bare program with no static class oh em gee what?

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
	loggingBuilder.AddConsoleFormatter<ImpunityConsoleFormatter,ImpunityConsoleFormatterOptions>(options => {});
});

ILogger serverLogger = loggerFactory.CreateLogger("ImpunityServer");
ILogger logger = loggerFactory.CreateLogger("Impunity");
ImpunityAspLogger.Setup(logger, logLevel);

serverLogger.LogInformation("Read configuration from " + configPath);


// --------------- Create app builder ---------------------

WebApplicationBuilder builder = WebApplication.CreateBuilder();

// --------------- Setup AspNetCore logger ---------------------

builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(aspLogLevel);
builder.Logging.AddConsole(options =>
{
	options.FormatterName = "consoleformat";
});
builder.Logging.AddConsoleFormatter<ImpunityConsoleFormatter,ImpunityConsoleFormatterOptions>(options => {});
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

WorldService worldService = new WorldService(config.datapath, config.worlds, impOptions);
builder.Services.AddSingleton(worldService);

TCPConnectionService? tcpConnectionService = null;
if (config.tcp_port != 0)
{
	tcpConnectionService = new TCPConnectionService(worldService, impOptions);
	builder.Services.AddSingleton(tcpConnectionService);
	tcpConnectionService.Start();
	serverLogger.LogInformation($"Listening for TCP on port {config.tcp_port}");
}

builder.Services.AddSingleton(new InfoService(impOptions));

// --------------- Setup AspNetCore web server ---------------------
builder.Services.AddSingleton<IHostLifetime, ConsoleLifetime>();

builder.Services.AddControllers().AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.WriteIndented = true;
    });

builder.Environment.ApplicationName = "ImpunityStandaloneServer";

WebApplication app = builder.Build();
app.UseWebSockets();

WebSocketOptions webSocketOptions = new()
{
	KeepAliveInterval = TimeSpan.FromMinutes(1)
};

app.Urls.Add("http://*:" + config.http_port);

app.UseWebSockets(webSocketOptions);

app.MapControllers();

app.Lifetime.ApplicationStarted.Register(()=>
{
	serverLogger.LogInformation("Listening for HTTP on port {port}" , config.http_port);
});

serverLogger.LogInformation("Impunity Standalone Server starting up");

// --------------- Run server ---------------------

app.Run();

// --------------- Shutdown server ---------------------

serverLogger.LogInformation("Shutting down");

if (tcpConnectionService != null)
{
	tcpConnectionService.Stop();
}

worldService.Stop();

serverLogger.LogInformation("Impunity Standalone Server done");