
using UltraLiteDB;

namespace Impunity.GameState
{

	public interface IServerMessageHandler
	{
		void HandleCreateChannel(uint channelId, string channelName, int channelType, byte[] propData);
		void HandleCreateObject(uint objectId, uint channelId, int objectType, byte[] propData);
		void HandleEntityUpdate(uint entityId, byte[] propData);
		void HandleEntityEvent(uint entityId, int eventType, BsonValue eventData);
		void HandleEntityDelete(uint entityId, BsonValue deleteData);

		void HandleBroadcastMessage(int messageType, BsonValue messageBody, string sentBy);
	}

}