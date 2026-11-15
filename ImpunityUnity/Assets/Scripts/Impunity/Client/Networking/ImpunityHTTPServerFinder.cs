using System;
using System.Collections.Generic;

using UltraLiteDB;

namespace Impunity.Networking
{

	/// <summary>Discovers game worlds on a remote standalone server via its HTTP API. Uses the platform-appropriate transport via <see cref="ImpunityHttp"/> (UnityWebRequest in Unity, HttpClient standalone).</summary>
	public static class ImpunityHTTPServerFinder
	{
		// The reply comes from whatever host the player entered, so it's mapped with a private mapper (defaults
		// matching BsonMapper.Global) that allows no _type names: the reply types are all concrete. Using
		// BsonMapper.Global would let an app loosen the allow list for its own data and widen this path too.
		private static readonly BsonMapper ReplyMapper = new BsonMapper();

		/// <summary>Queries an HTTP(S) server origin, e.g. <c>https://play.example.com:443</c>, for its worlds.
		/// Results retain a full <see cref="ServerInfo.WebSocketUrl"/> for browser connections. TLS validation uses the platform defaults.</summary>
		/// <param name="options">Expected game type and connection settings.</param>
		/// <param name="serverUrl">Full HTTP(S) URL, optionally with a port, without a path, credentials, query or fragment.</param>
		/// <param name="onComplete">Called with the discovered worlds or an error.</param>
		public static void GetServerWorlds(ImpunityOptions options, string serverUrl, ImpunityCallback<List<ServerInfo>> onComplete)
		{
			if (serverUrl == null) throw new ArgumentNullException(nameof(serverUrl));
			if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var serverUri) || (serverUri.Scheme != "http" && serverUri.Scheme != "https")
				|| serverUri.UserInfo.Length != 0 || serverUri.AbsolutePath != "/" || serverUri.Query.Length != 0 || serverUri.Fragment.Length != 0)
				throw new ArgumentException("Expected an HTTP(S) server origin without a path, credentials, query or fragment.", nameof(serverUrl));
			string url = new Uri(serverUri, "/worlds").AbsoluteUri;
			string webSocketUrl = new UriBuilder(serverUri)
			{
				Scheme = serverUri.Scheme == "https" ? "wss" : "ws", Port = serverUri.Port, Path = "/ws"
			}.Uri.AbsoluteUri;

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

					StandaloneServerWorldsInfo reply = ReplyMapper.ToObject<StandaloneServerWorldsInfo>(docReply);

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
						var info = new ServerInfo();
						info.WorldName = worldInfo.WorldName;
						info.Hostname = serverUri.Host;
						info.Port = reply.TCPPort ?? 0;
						info.WebSocketUrl = webSocketUrl;
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

		/// <summary>Queries an HTTP(S) server origin for a world by ID; returns its TCP details and full WebSocket URL.</summary>
		public static void GetServerWorldStatus(ImpunityOptions options, string serverUrl, string gameId, ImpunityCallback<ServerInfo> onComplete)
		{
			GetServerWorlds(options, serverUrl, (err, worlds) =>
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
