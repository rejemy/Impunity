#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace Impunity.StandaloneServer;

[ApiController]
public class InfoController(InfoService infoService, WorldService worldService, ConnectionService tcpService) : ControllerBase
{
	private readonly InfoService infoService = infoService;
	private readonly WorldService worldService = worldService;
	private readonly ConnectionService tcpService = tcpService;


	[HttpGet("/")]
	public ActionResult<ServerStatus> GetStatus()
	{
		return new ServerStatus
		{
			Status = "OK",
			Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
			Uptime = this.infoService.GetUptime()
		};
	}

	[HttpGet("info")]
	public ActionResult<ServerInfo> GetInfo()
	{
		var process = Process.GetCurrentProcess();

		return new ServerInfo
		{
			Status = "OK",
			Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
			Uptime = infoService.GetUptime(),
			DotnetVersion = Environment.Version.ToString(),
			Command = infoService.GetLaunchCommand(),
			MachineName = Environment.MachineName,
			HostName = System.Net.Dns.GetHostName(),
			RuntimeInfo = new RuntimeInfo
			{
				OperatingSystem = Environment.OSVersion.ToString(),
				Is64Bit = Environment.Is64BitProcess,
				AllocatedMemory = process.PrivateMemorySize64,
				GCMode = System.Runtime.GCSettings.IsServerGC ? "Server" : "Workstation",
				ProcessorCount = Environment.ProcessorCount
			}
		};
	}

	[HttpGet("worlds")]
	public ActionResult<StandaloneServerWorldsInfo> GetWorlds()
	{
		StandaloneServerWorldsInfo result = new StandaloneServerWorldsInfo();
		result.ImpunityVersion = ImpunityConstants.ImpunityVersion;
		result.GameType = infoService.Options.GameTypeCode;
		result.TCPPort = tcpService.Port;
		var worldDatas = worldService.GetWorldDatas();
		result.Worlds = new List<StandaloneServerWorldInfo>(worldDatas.Count);



		foreach (var worldData in worldDatas)
		{
			int currPlayers = tcpService.NumConnections();
			currPlayers = currPlayers <= worldData.MaxPlayers ? currPlayers : worldData.MaxPlayers;

			var gameMetadata = worldData.GameState.GetGameMetadata();

			var info = new StandaloneServerWorldInfo
			{
				WorldId = worldData.ID,
				WorldName = worldData.Name,
				PasswordProtected = worldData.Password != null,
				CurrentPlayers = currPlayers,
				MaxPlayers = worldData.MaxPlayers,
				GameSummary = worldData.GetGameSummaryAsJson(),
				DataFormatChecksum = gameMetadata.DataFormatChecksum
			};
			result.Worlds.Add(info);
		}

		return result;
	}

	[HttpGet("world/{worldId}")]
	public ActionResult<StandaloneServerWorldInfo> GetWorldInfo(string worldId)
	{
		var worldData = worldService.GetWorldData(worldId);
		if (worldData == null)
		{
			return NotFound();
		}

		int currPlayers = tcpService.NumConnections();
		currPlayers = currPlayers <= worldData.MaxPlayers ? currPlayers : worldData.MaxPlayers;

		var gameMetadata = worldData.GameState.GetGameMetadata();

		var info = new StandaloneServerWorldInfo
		{
			WorldId = worldData.ID,
			WorldName = worldData.Name,
			PasswordProtected = worldData.Password != null,
			CurrentPlayers = currPlayers,
			MaxPlayers = worldData.MaxPlayers,
			GameSummary = worldData.GetGameSummaryAsJson(),
			DataFormatChecksum = gameMetadata.DataFormatChecksum
		};

		return info;

	}
}
