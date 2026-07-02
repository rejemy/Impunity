using System;
using System.Collections.Generic;

using UltraLiteDB;

namespace Impunity.Networking
{

	/// <summary>Discovers game worlds on a remote standalone server via its HTTP API. Uses the platform-appropriate transport via <see cref="ImpunityHttp"/> (UnityWebRequest in Unity, HttpClient standalone).</summary>
	public static class ImpunityHTTPServerFinder
	{
		/// <summary>Queries a standalone server's HTTP endpoint for its list of available game worlds. Validates version and game type compatibility. Calls <paramref name="onComplete"/> with the results.</summary>
		/// <param name="options"></param>
		/// <param name="hostname">Server hostname, optionally with port (e.g., "example.com" or "example.com:29653").</param>
		/// <param name="onComplete"></param>
		public static void GetServerWorlds(ImpunityOptions options, string hostname, ImpunityCallback<List<ServerInfo>> onComplete)
		{
			// Preserve the bare host (without any :port) for use in the returned ServerInfo entries.
			int colonIndex = hostname.IndexOf(':');
			string bareHost = colonIndex < 0 ? hostname : hostname.Substring(0, colonIndex);

			if (colonIndex < 0)
			{
				hostname += ":" + ImpunityConstants.DefaultServerHttpPort;
			}
			string url = "http://" + hostname + "/worlds";

			ImpunityHttp.Instance.Get(url, (err, body) =>
			{
				if (err != null)
				{
					onComplete(err, null!);
					return;
				}

				if (string.IsNullOrEmpty(body))
				{
					onComplete(new ImpunityErrorResponse(ImpunityErrorCode.UnknownError, "Server didn't return a response"), null!);
					return;
				}

				try
				{
					BsonValue bsonReply = JsonSerializer.Deserialize(body);
					BsonDocument? docReply = bsonReply as BsonDocument;
					if (docReply == null)
					{
						onComplete(new ImpunityErrorResponse(ImpunityErrorCode.ServerVersionIncompatible, "Unknown server response"), null!);
						return;
					}

					StandaloneServerWorldsInfo reply = BsonMapper.Global.ToObject<StandaloneServerWorldsInfo>(docReply);

					if (reply.ImpunityVersion != ImpunityConstants.ImpunityVersion)
					{
						onComplete(new ImpunityErrorResponse(ImpunityErrorCode.ServerVersionIncompatible, "Incompatible Impunity version"), null!);
						return;
					}

					if (reply.GameType != options.GameTypeCode)
					{
						onComplete(new ImpunityErrorResponse(ImpunityErrorCode.ServerVersionIncompatible, "Server is for a different Impunity game"), null!);
						return;
					}

					List<ServerInfo> hostedGames = new List<ServerInfo>();

					foreach (var worldInfo in reply.Worlds)
					{
						if (reply.TCPPort == null)
						{
							continue;
						}

						var info = new ServerInfo();
						info.WorldName = worldInfo.WorldName;
						info.Hostname = bareHost;
						info.Port = reply.TCPPort.Value;
						info.GameId = worldInfo.WorldId;
						info.PasswordProtected = worldInfo.PasswordProtected;
						info.CurrentPlayers = worldInfo.CurrentPlayers;
						info.MaxPlayers = worldInfo.MaxPlayers;
						info.GameStateFormatVersion = worldInfo.GameVersion;
						info.GameStateFormatChecksum = worldInfo.DataFormatChecksum;
						info.GameSummary = worldInfo.GameSummary != null ? (BsonDocument)JsonSerializer.Deserialize(worldInfo.GameSummary) : null;
						hostedGames.Add(info);
					}

					try
					{
						onComplete?.Invoke(null, hostedGames);
					}
					catch (Exception ex)
					{
						ImpunityLogger.LogError("Exception in ImpunityHTTPServerFinder callback", ex);
					}

				}
				catch (Exception e)
				{
					onComplete(new ImpunityErrorResponse(ImpunityErrorCode.UnknownError, e.Message), null!);
					return;
				}
			});
		}

		/// <summary>Queries a standalone server for a specific game world's info by its ID. Returns an error if the world is not found.</summary>
		public static void GetServerWorldStatus(ImpunityOptions options, string hostname, string gameId, ImpunityCallback<ServerInfo> onComplete)
		{
			GetServerWorlds(options, hostname, (err, worlds) =>
			{
				if (err != null)
				{
					onComplete(err, null!);
					return;
				}

				foreach (var world in worlds)
				{
					if (world.GameId == gameId)
					{
						onComplete(null, world);
						return;
					}
				}

				onComplete(new ImpunityErrorResponse(ImpunityErrorCode.ActionNotFound, "Game not found on server"), null!);
			});
		}
	}

}
