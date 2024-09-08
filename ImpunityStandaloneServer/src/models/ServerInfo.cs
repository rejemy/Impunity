#nullable enable

using System;

namespace Impunity.StandaloneServer;

public class ServerInfo
{
	public string? Status {get; set;}
	public string? Version {get; set;}
	public TimeSpan Uptime {get; set;}

	public string? DotnetVersion {get; set;}

	public string? Command {get; set;}
	public string? MachineName {get; set;}
	public string? HostName {get; set;}

	public RuntimeInfo? RuntimeInfo {get; set;}
	
}

public class RuntimeInfo
{
	public string? OperatingSystem {get; set;}
	public bool Is64Bit {get; set;}
	public int ProcessorCount {get; set;}
	public long AllocatedMemory {get; set;}
	public string? GCMode {get; set;}
	
}