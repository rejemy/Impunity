using System;
using UltraLiteDB;

namespace Impunity.GameState
{

	/// <summary>Handler interface for server-to-client messages related to game state replication. Implemented by the client-side entity manager to process state updates.</summary>
	public interface IServerMessageHandler
	{
		/// <summary>Called when a new channel (subscription group) is created. <paramref name="seq"/> is the entity's current
		/// broadcast seq at snapshot time, used to seed per-field received-seqs for optimistic guarded updates.</summary>
		void HandleCreateChannel(uint channelId, string channelName, int channelType, bool isLocked, byte instanceFlags, ArraySegment<byte> propData, uint seq);
		/// <summary>Called when a new entity is created within a channel. <paramref name="seq"/> is the entity's current
		/// broadcast seq at snapshot time, used to seed per-field received-seqs for optimistic guarded updates.</summary>
		void HandleCreateObject(uint objectId, uint channelId, int objectType, bool isLocked, byte instanceFlags, ArraySegment<byte> propData, string? uniqueName, bool newlyCreated, uint seq);
		/// <summary>Called when an entity's properties are updated (dirty fields only).</summary>
		/// <summary>An entity's properties were updated. <paramref name="note"/> is the writer's note on the update (null
		/// for none), stamped <paramref name="noteSentMs"/>; <paramref name="noteIsOwn"/> is true when this connection
		/// wrote the update.</summary>
		void HandleEntityUpdate(uint entityId, ArraySegment<byte> propData, uint seq, BsonValue? note, long noteSentMs, bool noteIsOwn);
		/// <summary>Called when a custom event is fired on an entity. <paramref name="sentServerMs"/> is the server time
		/// the sender fired it at; <paramref name="isOwn"/> is true when this connection fired it.</summary>
		void HandleEntityEvent(uint entityId, int eventType, BsonValue eventData, long sentServerMs, bool isOwn);
		/// <summary>Called when an entity becomes locked by a connection.</summary>
		void HandleEntityLocked(uint entityId);
		/// <summary>Called when an entity lock is released.</summary>
		void HandleEntityUnlocked(uint entityId);
		/// <summary>Called when this connection is handed an entity's lock from the server's waiter queue.</summary>
		void HandleEntityLockGranted(uint entityId);
		/// <summary>Called when an entity is deleted. <paramref name="sentServerMs"/> is the server time the deleter sent
		/// the request at; <paramref name="isOwn"/> is true when this connection's request deleted or replaced it.</summary>
		void HandleEntityDelete(uint entityId, BsonValue? deleteData, long sentServerMs, bool isOwn);

		/// <summary>Called when a broadcast message is sent to all connections.</summary>
		void HandleBroadcastMessage(int messageType, BsonValue messageBody, string sentBy);
		/// <summary>Called when a named lock is released.</summary>
		void HandleNamedLockUnlocked(string lockName);
		/// <summary>Called when this connection is handed a named lock from the server's waiter queue.</summary>
		void HandleNamedLockGranted(string lockName);
	}

}
