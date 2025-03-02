using System;
using System.Collections.Generic;

using UnityEngine.Networking;

using UltraLiteDB;
using UnityEngine;
using System.Collections;

namespace Impunity.Networking
{

	public static class ImpunityHTTPServerFinder
	{
		public static void GetServerWorlds(MonoBehaviour runner, ImpunityOptions options, string hostname, ImpunityCallback<List<ServerInfo>> onComplete)
		{
			if (hostname.IndexOf(':') < 0)
			{
				hostname += ":"+ImpunityConstants.DefaultServerHttpPort;
			}
			string url = "http://"+hostname+"/worlds";

			UnityWebRequest request = UnityWebRequest.Get(url);
			runner.StartCoroutine(SendRequest(request, () =>
			{
				if(request.error != null)
				{
					onComplete(new ImpunityErrorResponse(ImpunityErrorCode.ClientUnableToConnectError, request.error), null);
					return;
				}

				if (string.IsNullOrEmpty(request.downloadHandler.text))
				{
					onComplete(new ImpunityErrorResponse(ImpunityErrorCode.UnknownError, "Server didn't return a response"), null);
					return;
				}
				
				try
				{
					BsonValue bsonReply = JsonSerializer.Deserialize(request.downloadHandler.text);
					StandaloneServerWorldsInfo reply = BsonMapper.Global.ToObject<StandaloneServerWorldsInfo>(bsonReply as BsonDocument);

					if (reply.ImpunityVersion != ImpunityConstants.ImpunityVersion)
					{
						onComplete(new ImpunityErrorResponse(ImpunityErrorCode.ServerVersionIncompatible, "Incompatible Impunity version"), null);
						return;
					}

					if (reply.GameType != options.GameTypeCode)
					{
						onComplete(new ImpunityErrorResponse(ImpunityErrorCode.ServerVersionIncompatible, "Server is for a different Impunity game"), null);
						return;
					}

					string hostname = request.uri.DnsSafeHost;

					List<ServerInfo> hostedGames = new List<ServerInfo>();

					foreach(var worldInfo in reply.Worlds)
					{
						if (reply.TCPPort == null)
						{
							continue;
						}

						var info = new ServerInfo();
						info.WorldName = worldInfo.WorldName;
						info.Hostname = hostname;
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
					onComplete(new ImpunityErrorResponse(ImpunityErrorCode.UnknownError, e.Message), null);
					return;
				}
			}));
		}

		static IEnumerator SendRequest(UnityWebRequest request, Action onCompleted)
    	{
			yield return request.SendWebRequest();
			onCompleted();
		}

		public static void GetServerWorldStatus(MonoBehaviour runner, ImpunityOptions options, string hostname, string gameId, ImpunityCallback<ServerInfo> onComplete)
		{
			GetServerWorlds(runner, options, hostname, (err, worlds) =>
			{
				if (err != null)
				{
					onComplete(err, null);
					return;
				}

				foreach(var world in worlds)
				{
					if (world.GameId == gameId)
					{
						onComplete(null, world);
						return;
					}
				}

				onComplete(new ImpunityErrorResponse(ImpunityErrorCode.ActionNotFound, "Game not found on server"), null);
			});
		}
	}

}