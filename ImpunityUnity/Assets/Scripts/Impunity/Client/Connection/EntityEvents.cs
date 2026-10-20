using UltraLiteDB;

namespace Impunity.Connection
{
	/// <summary>Who an entity event fired with <see cref="IDistributedEntity.TriggerEvent(int, BsonValue, EntityEventEcho, ImpunityCallback)"/>
	/// is delivered to. Either way it goes to every other client subscribed to the entity's channel.</summary>
	public enum EntityEventEcho : byte
	{
		/// <summary>The sending connection receives its own event back too (the original behavior).</summary>
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

		/// <summary>Builds a received event.</summary>
		public EntityEvent(int type, BsonValue data, long sentServerMs)
		{
			Type = type;
			Data = data;
			SentServerMs = sentServerMs;
		}
	}
}
