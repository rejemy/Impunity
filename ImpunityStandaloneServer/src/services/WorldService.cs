#nullable enable

using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using UltraLiteDB;

using Impunity.GameState;
using System.Linq;

namespace Impunity.StandaloneServer;


public class WorldData
{
	public readonly string ID;
	public readonly string BaseDirectory;
	public readonly string Name;
	public readonly string? Password;
	public readonly int MaxPlayers;


	public readonly GameStateServer GameState;


    public WorldData(string id, string baseDir, string name, string? password, int maxPlayers, ImpunityOptions options)
	{
		ID = id;
		BaseDirectory = baseDir;
		Name = name;
		Password = password;
		MaxPlayers = maxPlayers;
		GameState = GameStateServer.OpenOrCreate(ID, Password, BaseDirectory, null, options);
	}

	public string? GetGameSummaryAsJson()
	{
		BsonDocument? summary = GameState.GetGameSummary();
		if (summary == null)
		{
			return null;
		}
		return JsonSerializer.Serialize(summary);
	}
}

public class WorldService
{
	private readonly Regex WorldIdPattern = new("^[a-z0-9_]+$");
	private readonly ReadOnlyDictionary<string,WorldData> Worlds;

	public WorldService(string datapath, List<WorldConfig> worlds, ImpunityOptions options)
	{
		Directory.CreateDirectory(datapath);
		var worldsLoaded = new Dictionary<string, WorldData>();

		foreach (WorldConfig config in worlds)
		{
			config.id = config.id?.Trim().ToLower();

			if (string.IsNullOrEmpty(config.id))
			{
				ImpunityLogger.LogError("World with no id - all worlds must be given an id value");
				continue;
			}

			if (!WorldIdPattern.IsMatch(config.id))
			{
				ImpunityLogger.LogError("World id must only user letters, numbers or underscores");
				continue;
			}

			config.name = config.name?.Trim();
			if (string.IsNullOrEmpty(config.name))
			{
				ImpunityLogger.LogError("World " + config.id + " has no name");
				continue;
			}

			string worldpath = Path.Join(datapath, config.id);
			Directory.CreateDirectory(worldpath);

			int maxPlayers = config.max_players <= 0 ? 16 : config.max_players;
			
			WorldData world = new WorldData(config.id, worldpath, config.name, config.password, maxPlayers, options);

			worldsLoaded.Add(config.id, world);
		}

		Worlds = new ReadOnlyDictionary<string, WorldData>(worldsLoaded);
	}

	public WorldData? GetWorldData(string id)
	{
		if (Worlds == null)
		{
			return null;
		}

		if(Worlds.TryGetValue(id, out WorldData? worldData))
		{
			return worldData;
		}
		return null;
	}

	public List<WorldData> GetWorldDatas()
	{
		if (Worlds == null)
		{
			return new List<WorldData>();
		}
		
		return new List<WorldData>(Worlds.Values);
	}

	public List<GameStateServer> GetGameStateServers()
	{
		if (Worlds == null)
		{
			return new List<GameStateServer>();
		}
		
		return new List<GameStateServer>(Worlds.Select(x => x.Value.GameState));
	}

	public static string HashPassword(string pass)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(pass));
        return Convert.ToBase64String(hash);
    }

	public void Stop()
	{
		foreach (WorldData worldData in Worlds.Values)
		{
			worldData.GameState.Dispose();
		}
	}

}