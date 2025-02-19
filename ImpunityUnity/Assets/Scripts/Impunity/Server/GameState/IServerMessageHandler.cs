using System;
using UltraLiteDB;

namespace Impunity.GameState
{

	public interface IServerMessageHandler
	{
		void HandleCreateChannel(uint channelId, string channelName, int channelType, bool isLocked, byte instanceFlags, ArraySegment<byte> propData);
		void HandleCreateObject(uint objectId, uint channelId, int objectType, bool isLocked, byte instanceFlags, ArraySegment<byte> propData, string uniqueName, bool newlyCreated);
		void HandleEntityUpdate(uint entityId, ArraySegment<byte> propData);
		void HandleEntityEvent(uint entityId, int eventType, BsonValue eventData);
		void HandleEntityLocked(uint entityId);
		void HandleEntityUnlocked(uint entityId);
		void HandleEntityDelete(uint entityId, BsonValue deleteData);
		

		void HandleBroadcastMessage(int messageType, BsonValue messageBody, string sentBy);
		void HandleNamedLockUnlocked(string lockName);
	}

}