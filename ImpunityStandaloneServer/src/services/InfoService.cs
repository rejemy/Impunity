#nullable enable

using System;

namespace Impunity.StandaloneServer;

public class InfoService
{
	private readonly DateTimeOffset StartupTime = DateTimeOffset.UtcNow;

	public InfoService()
	{

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