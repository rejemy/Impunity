#nullable enable

using System;

namespace Impunity.StandaloneServer;

public class ServerStatus
{
	public string? Status {get; set;}
	public string? Version {get; set;}
	public TimeSpan Uptime {get; set;}
}
