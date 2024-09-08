#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace Impunity.StandaloneServer;

[ApiController]
public class InfoController(InfoService infoService, WorldService worldService) : ControllerBase
{
	private readonly InfoService infoService = infoService;
	private readonly WorldService worldService = worldService;

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
	public ActionResult<WorldsInfo> GetWorlds()
	{
		WorldsInfo result = new WorldsInfo();

		var worldDatas = worldService.GetWorldDatas();
		result.Worlds = new List<WorldInfo>(worldDatas.Count);

		foreach(var worldData in worldDatas)
		{
            var info = new WorldInfo
            {
                WorldId = worldData.ID,
                WorldName = worldData.Name,
                CurrentPlayers = 0,
                MaxPlayers = worldData.MaxPlayers
            };
            result.Worlds.Add(info);
		}

		return result;
	}

	[HttpGet("world/{worldId}")]
	public ActionResult<WorldInfo> GetWorldInfo(string worldId)
	{
		var worldData = worldService.GetWorldData(worldId);
		if (worldData == null)
		{
			return NotFound();
		}

		var info = new WorldInfo
            {
                WorldId = worldData.ID,
                WorldName = worldData.Name,
                CurrentPlayers = 0,
                MaxPlayers = worldData.MaxPlayers
            };
		
		return info;

	}
}