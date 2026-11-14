using System;
using System.Collections.Generic;
using System.Linq;

using UltraLiteDB;

namespace Impunity.GameState
{
	// ───────── Live-state inspection ─────────
	//
	// Read-only queries over the server's live memory: which channels are loaded and who is subscribed, what a channel
	// holds, one entity's current state (as wire bytes plus per-field bookkeeping), and who is connected. Clients reach
	// them through BaseGameConnection.DescribeChannels / DescribeChannel / GetEntitySnapshot / DescribeConnections; each
	// runs on the live thread like any other live action, so it sees a consistent moment. None of them loads anything:
	// a channel that isn't in live memory is reported as not found, never loaded to answer.
	//
	// The server has no idea what a custom-typed field means (it stores opaque bytes), so an entity snapshot carries the
	// same property bytes a subscriber gets on create, and the client decodes them with its own type registry
	// (ClientEntityManager.DecodeSnapshot). See docs/guides/DistributedEntities.md, "Inspecting live state".

	/// <summary>One live channel as the server sees it: identity, type, subscribers and size.</summary>
	public class ChannelInfo
	{
		/// <summary>The channel's entity id (the same id its subscribers' channel objects carry).</summary>
		[BsonField("id")]
		public uint Id;

		/// <summary>The channel's name.</summary>
		[BsonField("n")]
		public string Name = null!;

		/// <summary>The channel's distributed type id, 0 for a generic untyped channel.</summary>
		[BsonField("t")]
		public int TypeId;

		/// <summary>The channel's distributed type name, null for a generic untyped channel.</summary>
		[BsonField("tn")]
		public string? TypeName;

		/// <summary>The channel's <see cref="ImpunityInstanceFlags"/> (persisted, client-authoritative, delete-on-disconnect).</summary>
		[BsonField("f")]
		public byte Flags;

		/// <summary>Connection ids of the current subscribers.</summary>
		[BsonField("s")]
		public List<string> Subscribers = new List<string>();

		/// <summary>How many objects the channel holds.</summary>
		[BsonField("oc")]
		public int ObjectCount;

		/// <summary>Server time (ms) since which the channel has had no subscribers; 0 while it has some. An idle channel is
		/// reaped after <see cref="ImpunityOptions.IdleChannelTimeoutMillis"/>.</summary>
		[BsonField("is")]
		public long IdleSinceMs;

		/// <summary>Connection id holding the channel's own lock, or null.</summary>
		[BsonField("lb")]
		public string? LockedBy;

		/// <summary>True when the channel is stored in the database.</summary>
		[BsonIgnore]
		public bool IsPersisted => ((ImpunityInstanceFlags)Flags & ImpunityInstanceFlags.Persisted) != 0;

		/// <summary>Number of current subscribers.</summary>
		[BsonIgnore]
		public int SubscriberCount => Subscribers?.Count ?? 0;
	}

	/// <summary>One entity's identity and bookkeeping (not its field values; see <see cref="EntitySnapshot"/>).</summary>
	public class EntityInfo
	{
		/// <summary>The entity id, as every client sees it (<c>IDistributedEntity.DistributedEntityId</c>).</summary>
		[BsonField("id")]
		public uint Id;

		/// <summary>The distributed type id.</summary>
		[BsonField("t")]
		public int TypeId;

		/// <summary>The distributed type name (the class name the type was registered under).</summary>
		[BsonField("tn")]
		public string? TypeName;

		/// <summary>An object's unique name in its channel, or a channel's name; null for an unnamed object.</summary>
		[BsonField("n")]
		public string? Name;

		/// <summary>True for a channel, false for an object.</summary>
		[BsonField("ch")]
		public bool IsChannel;

		/// <summary>The <see cref="ImpunityInstanceFlags"/> the entity was created with.</summary>
		[BsonField("f")]
		public byte Flags;

		/// <summary>Connection id holding the entity's lock, or null.</summary>
		[BsonField("lb")]
		public string? LockedBy;

		/// <summary>How many connections are queued for the lock.</summary>
		[BsonField("lw")]
		public int LockWaiters;

		/// <summary>Connection id that owns a delete-on-disconnect entity, or null.</summary>
		[BsonField("eo")]
		public string? EphemeralOwner;

		/// <summary>The entity's broadcast seq: stamped on the latest update relayed to its subscribers (0 = never
		/// updated since it was loaded).</summary>
		[BsonField("sq")]
		public uint Seq;

		/// <summary>True when the entity is stored in the database.</summary>
		[BsonIgnore]
		public bool IsPersisted => ((ImpunityInstanceFlags)Flags & ImpunityInstanceFlags.Persisted) != 0;

		/// <summary>True when the entity's owner writes it without server echoes.</summary>
		[BsonIgnore]
		public bool IsClientAuthoritative => ((ImpunityInstanceFlags)Flags & ImpunityInstanceFlags.ClientAuthoritative) != 0;

		/// <summary>True when the entity is deleted once its owner disconnects.</summary>
		[BsonIgnore]
		public bool IsDeleteOnDisconnect => ((ImpunityInstanceFlags)Flags & ImpunityInstanceFlags.DeleteOnDisconnect) != 0;
	}

	/// <summary>A live channel and every object in it.</summary>
	public class ChannelContents
	{
		/// <summary>The channel itself.</summary>
		[BsonField("c")]
		public ChannelInfo Channel = null!;

		/// <summary>The channel's objects, by ascending id.</summary>
		[BsonField("e")]
		public List<EntityInfo> Objects = new List<EntityInfo>();
	}

	/// <summary>The server's bookkeeping for one field of an entity snapshot.</summary>
	public class FieldSnapshot
	{
		/// <summary>The field's wire id.</summary>
		[BsonField("id")]
		public byte FieldId;

		/// <summary>The field's name as registered by the client format (the C# field name).</summary>
		[BsonField("n")]
		public string Name = null!;

		/// <summary>The entity broadcast seq that last modified this field (0 = not modified since the entity was
		/// loaded or created). A client whose received seq for the field is behind this hasn't seen the change yet.</summary>
		[BsonField("ms")]
		public uint LastModSeq;

		/// <summary>Server time (ms) of the field's last modification (0 = never since load).</summary>
		[BsonField("mt")]
		public long ModifiedMs;

		/// <summary>Connection id whose update last set the field, or null (unknown: set at create or load, or the
		/// writer has since resubscribed).</summary>
		[BsonField("w")]
		public string? LastWriter;

		/// <summary>That writer's own update seq for the field (its <c>SendSeq</c> when it sent it), 0 if unknown.</summary>
		[BsonField("ws")]
		public uint WriterSeq;
	}

	/// <summary>One entity's state on the server at one moment. <see cref="Props"/> is in the wire format a subscriber
	/// receives on create; decode it on a client with <c>ClientEntityManager.DecodeSnapshot</c>.</summary>
	public class EntitySnapshot
	{
		/// <summary>The entity's identity and lock/owner state.</summary>
		[BsonField("i")]
		public EntityInfo Info = null!;

		/// <summary>The channel the entity belongs to (its own name for a channel).</summary>
		[BsonField("c")]
		public string? ChannelName;

		/// <summary>The id of that channel.</summary>
		[BsonField("ci")]
		public uint ChannelId;

		/// <summary>Server time (ms) the snapshot was taken at.</summary>
		[BsonField("at")]
		public long ServerTimeMs;

		/// <summary>Every field's value, in the create wire format (field id, temporal age, value; 0-terminated).</summary>
		[BsonField("p")]
		public byte[] Props = Array.Empty<byte>();

		/// <summary>Per-field bookkeeping, by ascending field id.</summary>
		[BsonField("fs")]
		public List<FieldSnapshot> Fields = new List<FieldSnapshot>();

		/// <summary>The bookkeeping for one field, or null.</summary>
		public FieldSnapshot? GetField(byte fieldId)
		{
			foreach (FieldSnapshot field in Fields)
			{
				if (field.FieldId == fieldId)
				{
					return field;
				}
			}
			return null;
		}
	}

	/// <summary>One connection to the server: what it subscribes to and what it holds.</summary>
	public class ConnectionInfo
	{
		/// <summary>The connection id (as <c>BaseGameConnection.ConnectionId</c> reports it on that client).</summary>
		[BsonField("id")]
		public string Id = null!;

		/// <summary>False for an in-process local connection (the host's own), true for a network one.</summary>
		[BsonField("r")]
		public bool IsRemote;

		/// <summary>Whether the connection can receive unguaranteed (datagram) updates.</summary>
		[BsonField("u")]
		public bool SupportsUnguaranteed;

		/// <summary>Names of the channels it subscribes to.</summary>
		[BsonField("s")]
		public List<string> Subscriptions = new List<string>();

		/// <summary>Ids of the entities whose locks it holds (named locks included).</summary>
		[BsonField("l")]
		public List<uint> LocksHeld = new List<uint>();

		/// <summary>Ids of the delete-on-disconnect entities it owns.</summary>
		[BsonField("e")]
		public List<uint> Ephemerals = new List<uint>();
	}

	public abstract partial class GameStateEntity
	{
		/// <summary>This entity's identity and lock state for inspection.</summary>
		public EntityInfo DescribeForInspection()
		{
			return new EntityInfo
			{
				Id = Id,
				TypeId = TypeInfo?.Index ?? 0,
				TypeName = TypeInfo?.Name,
				Name = Name,
				IsChannel = this is GameStateChannel,
				Flags = (byte)Flags,
				LockedBy = LockHeldBy?.Id,
				LockWaiters = LockWaiters?.Count ?? 0,
				EphemeralOwner = EphemeralOwner?.Id,
				Seq = OutSeq,
			};
		}

		/// <summary>This entity's current state for inspection: its property bytes plus per-field bookkeeping.</summary>
		public EntitySnapshot SnapshotForInspection()
		{
			EntitySnapshot snapshot = new EntitySnapshot
			{
				Info = DescribeForInspection(),
				ChannelName = ChannelName,
				ServerTimeMs = GameStateServer.GetServerTime(),
			};

			if (this is GameStateObject obj)
			{
				snapshot.ChannelId = obj.Channel?.Id ?? 0;
			}
			else if (this is GameStateChannel)
			{
				snapshot.ChannelId = Id;
			}

			ArraySegment<byte> props = GetPropBytes();
			snapshot.Props = props.Array == null ? Array.Empty<byte>() : props.ToArray();

			if (TypeInfo?.PackedProperties != null)
			{
				foreach (GameStateEntityPropertyDef prop in TypeInfo.PackedProperties)
				{
					int index = prop.Index;
					snapshot.Fields.Add(new FieldSnapshot
					{
						FieldId = (byte)index,
						Name = prop.Name,
						LastModSeq = LastModSeq != null ? LastModSeq[index] : 0,
						ModifiedMs = Properties[index]?.LastModifiedTime ?? 0,
						LastWriter = LastWriter?[index]?.Id,
						WriterSeq = RecvSeq != null && LastWriter?[index] != null ? RecvSeq[index] : 0,
					});
				}
			}

			return snapshot;
		}
	}

	public partial class GameStateChannel
	{
		/// <summary>This channel's summary for inspection.</summary>
		public ChannelInfo DescribeChannelForInspection()
		{
			List<string> subscribers = Listeners.Keys.ToList();
			subscribers.Sort(StringComparer.Ordinal);

			return new ChannelInfo
			{
				Id = Id,
				Name = ChannelName,
				TypeId = TypeInfo?.Index ?? 0,
				TypeName = TypeInfo?.Name,
				Flags = (byte)Flags,
				Subscribers = subscribers,
				ObjectCount = Members.Count,
				IdleSinceMs = IdleSinceMillis,
				LockedBy = LockHeldBy?.Id,
			};
		}

		/// <summary>This channel and its objects for inspection.</summary>
		public ChannelContents DescribeContentsForInspection()
		{
			ChannelContents contents = new ChannelContents { Channel = DescribeChannelForInspection() };
			foreach (GameStateObject obj in Members.Values.OrderBy(o => o.Id))
			{
				contents.Objects.Add(obj.DescribeForInspection());
			}
			return contents;
		}
	}

	public partial class GameStateLive
	{
		/// <summary>Every channel in live memory, by name. <paramref name="namePrefix"/> keeps only names starting with
		/// it (ordinal); null or empty keeps all. Channels still loading from the database aren't included.</summary>
		public List<ChannelInfo> DescribeChannels(string? namePrefix = null)
		{
			List<ChannelInfo> result = new List<ChannelInfo>();
			foreach (GameStateChannel channel in Channels)
			{
				if (!string.IsNullOrEmpty(namePrefix) && !channel.ChannelName.StartsWith(namePrefix, StringComparison.Ordinal))
				{
					continue;
				}
				result.Add(channel.DescribeChannelForInspection());
			}
			result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
			return result;
		}

		/// <summary>A live channel and its objects. Throws <see cref="ImpunityErrorCode.ActionNotFound"/> when no channel
		/// of that name is in live memory (never loads one to answer).</summary>
		public ChannelContents DescribeChannel(string channelName)
		{
			if (channelName == null || !(NamedEntities.GetValueOrDefault(channelName) is GameStateChannel channel))
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionNotFound, "No live channel named '" + channelName + "'");
			}
			return channel.DescribeContentsForInspection();
		}

		/// <summary>One live entity's state. Throws <see cref="ImpunityErrorCode.ActionNotFound"/> for an id that isn't
		/// a live channel or object (named locks and channels still loading have no state to show).</summary>
		public EntitySnapshot SnapshotEntity(uint entityId)
		{
			GameStateEntity? entity = AllEntities.GetValueOrDefault(entityId);
			if (!(entity is GameStateChannel) && !(entity is GameStateObject))
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionNotFound, "No live entity with id " + entityId);
			}
			return entity.SnapshotForInspection();
		}

		/// <summary>Every connection the server has, by id.</summary>
		public List<ConnectionInfo> DescribeConnections()
		{
			List<ConnectionInfo> result = new List<ConnectionInfo>();
			foreach (GameStateReplicant replica in ConnectedReplicas)
			{
				ConnectionInfo info = new ConnectionInfo
				{
					Id = replica.Id,
					IsRemote = replica.ConnectionProxy.IsRemote,
					SupportsUnguaranteed = replica.ConnectionProxy.SupportsUnguaranteed,
				};
				foreach (GameStateChannel channel in replica.Subscriptions.Values)
				{
					info.Subscriptions.Add(channel.ChannelName);
				}
				info.Subscriptions.Sort(StringComparer.Ordinal);
				info.LocksHeld.AddRange(replica.LocksHeld.Keys.OrderBy(id => id));
				info.Ephemerals.AddRange(replica.EphemeralEntities.Keys.OrderBy(id => id));
				result.Add(info);
			}
			result.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
			return result;
		}
	}
}
