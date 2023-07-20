
using UltraLiteDB;

namespace Impunity.GameState
{

	public interface IServerMessageHandler
	{
		void HandleCreateChannel(uint channelId, string channelName, int channelType);
		void HandleCreateObject(uint objectId, uint channelId, int objectType);
		void HandleEntityUpdate(uint entityId);
		void HandleEntityEvent(uint entityId, int eventType, BsonValue eventData);
		void HandleEntityDelete(uint entityId);

		void HandleBroadcastMessage(int messageType, BsonValue messageBody, string sentBy);
	}

}