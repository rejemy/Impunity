#nullable enable

using System;

namespace Impunity.StandaloneServer;

public class InfoService
{
	private readonly DateTimeOffset StartupTime = DateTimeOffset.UtcNow;
	public ImpunityOptions Options { get; init; }

	public InfoService(ImpunityOptions options)
	{
		Options = options;
	}

	public TimeSpan GetUptime()
	{
		return DateTimeOffset.UtcNow - StartupTime;
	}

	public string GetLaunchCommand()
	{
		return string.Join(" ", Environment.GetCommandLineArgs());
	}

	
}