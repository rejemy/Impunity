#nullable enable

using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Impunity.StandaloneServer;

public sealed class ImpunityConsoleFormatterOptions : ConsoleFormatterOptions
{
}

public sealed class ImpunityConsoleFormatter : ConsoleFormatter, IDisposable
{
	private readonly IDisposable? _optionsReloadToken;
	private ImpunityConsoleFormatterOptions _formatterOptions;

	public ImpunityConsoleFormatter(IOptionsMonitor<ImpunityConsoleFormatterOptions> options) : base("consoleformat")
	{
		_optionsReloadToken = options.OnChange(ReloadLoggerOptions);
		_formatterOptions = options.CurrentValue;
	}

	private void ReloadLoggerOptions(ImpunityConsoleFormatterOptions options)
	{
		_formatterOptions = options;
	}

	public override void Write<TState>(
		in LogEntry<TState> logEntry,
		IExternalScopeProvider? scopeProvider,
		TextWriter textWriter)
	{
		string message = logEntry.Formatter.Invoke(logEntry.State, logEntry.Exception);

		switch (logEntry.LogLevel)
		{
			case LogLevel.Trace:
				textWriter.Write("[Trace] ");
				break;
			case LogLevel.Debug:
				textWriter.Write("[Debug] ");
				break;
			case LogLevel.Information:
				textWriter.Write("[Info]  ");
				break;
			case LogLevel.Warning:
				textWriter.Write("[Warn]  ");
				break;
			case LogLevel.Error:
				textWriter.Write("[Error] ");
				break;
			case LogLevel.Critical:
				textWriter.Write("[Crit]  ");
				break;
			case LogLevel.None:
				textWriter.Write("        ");
				break;
		}

		textWriter.WriteLine(message);
	}

	public void Dispose()
	{
		_optionsReloadToken?.Dispose();
	}
}
