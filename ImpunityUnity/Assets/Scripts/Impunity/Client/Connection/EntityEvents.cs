using UltraLiteDB;

namespace Impunity.Connection
{
	/// <summary>Who an entity event fired with <see cref="IDistributedEntity.TriggerEvent(int, BsonValue, EntityEventEcho, ImpunityCallback)"/>
	/// is delivered to. Either way it goes to every other client subscribed to the entity's channel.</summary>
	public enum EntityEventEcho : byte
	{
		/// <summary>The sending connection receives its own event back too, with <see cref="EntityEvent.IsOwn"/> set.</summary>
		IncludeSender = 0,

		/// <summary>The sending connection doesn't receive its own event: the usual choice when the sender acts on the
		/// event itself the moment it fires it, so it has no echo to recognize and skip.</summary>
		ExcludeSender = 1,
	}

	/// <summary>One entity event as a client receives it (<see cref="IDistributedEntity.OnEventTriggered"/>).</summary>
	public readonly struct EntityEvent
	{
		/// <summary>The application-defined event type id supplied by the sender.</summary>
		public readonly int Type;

		/// <summary>The BSON payload supplied by the sender. May be null.</summary>
		public readonly BsonValue Data;

		/// <summary>Server time (Unix ms, as <see cref="BaseGameConnection.GetServerTime"/>) at which the sender fired the
		/// event: its estimate of server time at the send, or, from a sender that didn't stamp it, the server's clock when
		/// it relayed the event. Compare with this client's <see cref="BaseGameConnection.GetServerTime"/> to tell how late
		/// the event arrived.</summary>
		public readonly long SentServerMs;

		/// <summary>True when this connection fired the event and is receiving it back (it did not pass
		/// <see cref="EntityEventEcho.ExcludeSender"/>). Usually a reason to skip presentation the sender already played.</summary>
		public readonly bool IsOwn;

		/// <summary>Builds a received event.</summary>
		public EntityEvent(int type, BsonValue data, long sentServerMs, bool isOwn)
		{
			Type = type;
			Data = data;
			SentServerMs = sentServerMs;
			IsOwn = isOwn;
		}
	}

	/// <summary>An entity's deletion as a client receives it (<see cref="IDistributedEntity.OnDeleted"/>): the cause the
	/// deleter attached, when it was sent, and whether this connection did it. The same three members, with the same
	/// meaning, as <see cref="EntityEvent"/> and <see cref="UpdateNote"/>.</summary>
	public readonly struct EntityDeletion
	{
		/// <summary>The payload the deleter supplied (<c>deleteData</c> on a delete, <c>replaceData</c> on a replace), or
		/// null: none was given, or the server removed the entity itself.</summary>
		public readonly BsonValue? Data;

		/// <summary>Server time (Unix ms, as <see cref="BaseGameConnection.GetServerTime"/>) at which the deleter sent the
		/// request: its estimate of server time at the send, or the server's own clock for a removal the server made.</summary>
		public readonly long SentServerMs;

		/// <summary>True when this connection's own request deleted or replaced the entity.</summary>
		public readonly bool IsOwn;

		/// <summary>Builds a received deletion.</summary>
		public EntityDeletion(BsonValue? data, long sentServerMs, bool isOwn)
		{
			Data = data;
			SentServerMs = sentServerMs;
			IsOwn = isOwn;
		}
	}
}
