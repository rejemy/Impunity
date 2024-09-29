using System;
using System.Collections.Generic;

using UnityEngine.Networking;

using UltraLiteDB;

namespace Impunity.Networking
{

	public static class ImpunityHTTPServerFinder
	{
		public static void GetServerWorlds(ImpunityOptions options, string url, ImpunityCallback<List<ServerInfo>> onComplete)
		{
			UnityWebRequest request = UnityWebRequest.Get(url);
			var asyncOp = request.SendWebRequest();
			asyncOp.completed += _ =>
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

				}
				catch (Exception e)
				{
					onComplete(new ImpunityErrorResponse(ImpunityErrorCode.UnknownError, e.Message), null);
					return;
				}
			};
		}
	}

}