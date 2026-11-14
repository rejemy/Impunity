using System;
using System.Collections.Generic;
using System.Text;

using Impunity.GameState;
using UltraLiteDB;

namespace Impunity.Connection
{
	// ───────── Client-side state inspection ─────────
	//
	// Reading this client's replicated state by field name, decoding a server EntitySnapshot with this client's own type
	// registry, and comparing the two. The comparison uses the entity's sequence numbers to tell a real desync from a
	// change that is merely on its way: see FieldSyncState. Guide: docs/guides/DistributedEntities.md, "Inspecting live
	// state".

	/// <summary>How one field of this client's copy of an entity compares with the server's (see
	/// <see cref="ClientEntityManager.CompareWithSnapshot"/>).</summary>
	public enum FieldSyncState
	{
		/// <summary>The values are equal.</summary>
		Match,
		/// <summary>The values differ although this client has received the update that last changed the field on the
		/// server (or a later one it then lost): the two copies disagree about the same change. A desync.</summary>
		Mismatch,
		/// <summary>The server changed the field after the last update this client received for it: the change is on its
		/// way, or it was an unguaranteed update that got dropped. Check again; if it persists, the update was lost.</summary>
		InFlightToClient,
		/// <summary>This client received a change to the field after the snapshot was taken, so the snapshot is older
		/// than the client's copy. Check again.</summary>
		ChangedSinceSnapshot,
		/// <summary>This client wrote the field and the server hadn't applied the write when the snapshot was taken: it
		/// is still dirty (unsent), or this client owns a client-authoritative entity and its update hadn't arrived.</summary>
		InFlightToServer,
	}

	/// <summary>One field's comparison in an <see cref="EntitySyncReport"/>.</summary>
	public class FieldSyncResult
	{
		/// <summary>The field's wire id.</summary>
		public byte FieldId;
		/// <summary>The field's name (the C# field name).</summary>
		public string Name = null!;
		/// <summary>How the two copies compare.</summary>
		public FieldSyncState State;
		/// <summary>This client's value.</summary>
		public BsonValue ClientValue = BsonValue.Null;
		/// <summary>The server's value at the snapshot.</summary>
		public BsonValue ServerValue = BsonValue.Null;
		/// <summary>The broadcast seq of the last update this client applied to the field (0 = none received).</summary>
		public uint ClientSeq;
		/// <summary>The broadcast seq that last changed the field on the server (0 = not changed since load).</summary>
		public uint ServerSeq;
		/// <summary>Where the values differ, one line per difference ("path: client ≠ server"); empty when they match.</summary>
		public List<string> Differences = new List<string>();
	}

	/// <summary>The result of comparing this client's copy of an entity with a server snapshot of it.</summary>
	public class EntitySyncReport
	{
		/// <summary>The entity compared.</summary>
		public IDistributedEntity Entity = null!;
		/// <summary>The server's snapshot it was compared with.</summary>
		public EntitySnapshot Snapshot = null!;
		/// <summary>One result per field, by ascending field id.</summary>
		public List<FieldSyncResult> Fields = new List<FieldSyncResult>();

		/// <summary>True when no field is a <see cref="FieldSyncState.Mismatch"/>. Fields in flight either way don't
		/// count against it; check <see cref="IsSettled"/> to know nothing was in flight either.</summary>
		public bool InSync => Count(FieldSyncState.Mismatch) == 0;

		/// <summary>True when every field matches.</summary>
		public bool IsSettled => Count(FieldSyncState.Match) == Fields.Count;

		/// <summary>How many fields are in <paramref name="state"/>.</summary>
		public int Count(FieldSyncState state)
		{
			int n = 0;
			foreach (FieldSyncResult field in Fields)
			{
				if (field.State == state)
				{
					n++;
				}
			}
			return n;
		}
	}

	/// <summary>Path-by-path differences between two BSON values, for showing a desync to a person.</summary>
	public static class BsonDiff
	{
		/// <summary>Appends one line per difference between <paramref name="a"/> and <paramref name="b"/> to
		/// <paramref name="into"/> ("path: a ≠ b", or "path: only in a/b" for a document key or array element one side
		/// lacks), up to <paramref name="max"/> lines in total. Numbers of different BSON types compare by value.</summary>
		public static void Collect(BsonValue? a, BsonValue? b, string path, List<string> into, int max = 20)
		{
			a ??= BsonValue.Null;
			b ??= BsonValue.Null;
			if (into.Count >= max || Same(a, b))
			{
				return;
			}

			if (a.IsDocument && b.IsDocument)
			{
				BsonDocument da = a.AsDocument!;
				BsonDocument db = b.AsDocument!;
				foreach (string key in da.Keys)
				{
					string sub = Join(path, key);
					if (!db.ContainsKey(key))
					{
						Add(into, max, sub + ": only on the client side (" + Show(da[key]) + ")");
						continue;
					}
					Collect(da[key], db[key], sub, into, max);
				}
				foreach (string key in db.Keys)
				{
					if (!da.ContainsKey(key))
					{
						Add(into, max, Join(path, key) + ": only on the server side (" + Show(db[key]) + ")");
					}
				}
				return;
			}

			if (a.IsArray && b.IsArray)
			{
				BsonArray aa = a.AsArray!;
				BsonArray ab = b.AsArray!;
				int common = Math.Min(aa.Count, ab.Count);
				for (int i = 0; i < common; i++)
				{
					Collect(aa[i], ab[i], path + "[" + i + "]", into, max);
				}
				if (aa.Count != ab.Count)
				{
					Add(into, max, (path.Length == 0 ? "(value)" : path) + ": length " + aa.Count + " ≠ " + ab.Count);
				}
				return;
			}

			Add(into, max, (path.Length == 0 ? "(value)" : path) + ": " + Show(a) + " ≠ " + Show(b));
		}

		/// <summary>True when the two values are equal, deeply (numbers of different BSON types by value).</summary>
		public static bool Same(BsonValue? a, BsonValue? b)
		{
			a ??= BsonValue.Null;
			b ??= BsonValue.Null;
			try
			{
				return a.CompareTo(b) == 0;
			}
			catch (NotImplementedException)
			{
				return JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
			}
		}

		/// <summary>A value as short JSON (binary as its length, long strings and containers truncated).</summary>
		public static string Show(BsonValue? value)
		{
			value ??= BsonValue.Null;
			if (value.IsBinary)
			{
				return "<" + value.AsBinary.Count + " bytes>";
			}
			string json = JsonSerializer.Serialize(value);
			return json.Length > 120 ? json.Substring(0, 117) + "..." : json;
		}

		static string Join(string path, string key) => path.Length == 0 ? key : path + "." + key;

		static void Add(List<string> into, int max, string line)
		{
			if (into.Count < max)
			{
				into.Add(line);
			}
		}
	}

	public partial class ClientEntityManager
	{
		/// <summary>Every entity this manager has registered (subscribed channels and their objects), by id.</summary>
		public IReadOnlyList<IDistributedEntity> GetRegisteredEntities()
		{
			List<IDistributedEntity> all = new List<IDistributedEntity>(DistributedObjects.Values);
			all.Sort((a, b) => a.DistributedEntityId.CompareTo(b.DistributedEntityId));
			return all;
		}

		/// <summary>The registered entity with this id, or null.</summary>
		public IDistributedEntity? FindEntity(uint entityId)
		{
			return DistributedObjects.GetValueOrDefault(entityId);
		}

		/// <summary>The registered type name for a distributed type id, or null if this manager doesn't know it.</summary>
		public string? GetTypeName(int typeId)
		{
			if (typeId <= 0 || DistributedTypes == null || typeId >= DistributedTypes.Length)
			{
				return null;
			}
			return DistributedTypes[typeId]?.ObjectType.Name;
		}

		/// <summary>Every distributed field of <paramref name="entity"/> as BSON, keyed by field name (persisted or not,
		/// temporal fields as their current value), in field-id order: this client's view of its replicated state.
		/// Throws if the entity's type isn't registered with this manager.</summary>
		public BsonDocument GetFieldsAsBson(IDistributedEntity entity)
		{
			DistributedTypeInfo typeInfo = GetRegisteredTypeInfo(entity);
			BsonDocument doc = new BsonDocument();
			foreach (DistributedTypeFieldInfo fieldInfo in typeInfo.DistributedFields)
			{
				if (fieldInfo == null) continue;
				doc[fieldInfo.FieldName] = ReadFieldAsBson(entity, fieldInfo);
			}
			return doc;
		}

		/// <summary>Decodes a server snapshot's field values with this client's type registry, keyed by field name, in
		/// field-id order — the same shape as <see cref="GetFieldsAsBson"/>, so the two can be compared. Throws if the
		/// snapshot's type isn't registered with this manager. A generic untyped channel decodes to an empty document.</summary>
		public BsonDocument DecodeSnapshot(EntitySnapshot snapshot)
		{
			IDistributedEntity? decoded = DecodeSnapshotEntity(snapshot);
			return decoded == null ? new BsonDocument() : GetFieldsAsBson(decoded);
		}

		/// <summary>Builds a detached instance of the snapshot's type holding the server's values: not registered,
		/// no manager, never sent anywhere — for typed comparisons in tests and tools. Null for a generic untyped
		/// channel, which has no fields. Throws if the snapshot's type isn't registered with this manager.</summary>
		public IDistributedEntity? DecodeSnapshotEntity(EntitySnapshot snapshot)
		{
			if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
			int typeId = snapshot.Info.TypeId;
			if (typeId == 0)
			{
				return null;
			}

			DistributedTypeInfo typeInfo = GetDistributedTypeInfo(typeId);
			IDistributedEntity entity = typeInfo.Factory != null
				? typeInfo.Factory()
				: (IDistributedEntity)Activator.CreateInstance(typeInfo.ObjectType);
			entity.DistributedEntityType = typeId;
			if (snapshot.Props != null && snapshot.Props.Length > 0)
			{
				SetPropertyBytes(entity, new ArraySegment<byte>(snapshot.Props), true);
			}
			return entity;
		}

		/// <summary>Compares this client's copy of <paramref name="entity"/> with a server snapshot of it, field by field,
		/// classifying each difference by sequence numbers (see <see cref="FieldSyncState"/>): a field only counts as
		/// a <see cref="FieldSyncState.Mismatch"/> when this client has seen the server's latest change to it and still
		/// holds a different value. Pure: sends nothing. Throws if the snapshot is of another entity.</summary>
		public EntitySyncReport CompareWithSnapshot(IDistributedEntity entity, EntitySnapshot snapshot)
		{
			if (entity == null) throw new ArgumentNullException(nameof(entity));
			if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
			if (snapshot.Info.Id != entity.DistributedEntityId || snapshot.Info.TypeId != entity.DistributedEntityType)
			{
				throw new ArgumentException("Snapshot of entity " + snapshot.Info.Id + " (type " + snapshot.Info.TypeId
					+ ") compared with entity " + entity.DistributedEntityId + " (type " + entity.DistributedEntityType + ")");
			}

			EntitySyncReport report = new EntitySyncReport { Entity = entity, Snapshot = snapshot };
			BsonDocument server = DecodeSnapshot(snapshot);
			DistributedTypeInfo typeInfo = GetRegisteredTypeInfo(entity);
			uint snapshotSeq = snapshot.Info.Seq;

			// The newest of this connection's own updates the server had applied when it took the snapshot.
			uint newestOwnWrite = 0;
			string? me = Connection?.ConnectionId;
			foreach (FieldSnapshot field in snapshot.Fields)
			{
				if (me != null && field.LastWriter == me && ImpunityUtil.SeqIsNewer(field.WriterSeq, newestOwnWrite))
				{
					newestOwnWrite = field.WriterSeq;
				}
			}

			foreach (DistributedTypeFieldInfo fieldInfo in typeInfo.DistributedFields)
			{
				if (fieldInfo == null) continue;

				FieldSnapshot? serverField = snapshot.GetField(fieldInfo.FieldId);
				FieldSyncResult result = new FieldSyncResult
				{
					FieldId = fieldInfo.FieldId,
					Name = fieldInfo.FieldName,
					ClientValue = ReadFieldAsBson(entity, fieldInfo),
					ServerValue = server.ContainsKey(fieldInfo.FieldName) ? server[fieldInfo.FieldName] : BsonValue.Null,
					ClientSeq = entity.FieldRecvSeq != null && fieldInfo.FieldId < entity.FieldRecvSeq.Length ? entity.FieldRecvSeq[fieldInfo.FieldId] : 0,
					ServerSeq = serverField?.LastModSeq ?? 0,
				};

				BsonDiff.Collect(result.ClientValue, result.ServerValue, "", result.Differences);
				result.State = Classify(entity, fieldInfo, result, snapshotSeq, newestOwnWrite);
				report.Fields.Add(result);
			}

			return report;
		}

		/// <summary>Fetches a snapshot of <paramref name="entity"/> from the server and compares it with this client's
		/// copy (<see cref="CompareWithSnapshot"/>) when the reply arrives. Fails if the entity isn't registered with
		/// this manager or has no connection, if the server refuses the snapshot, or if the entity was unregistered
		/// before the reply. Delivered on a later <see cref="BaseGameConnection.Update"/>, never reentrantly.</summary>
		public void CheckSync(IDistributedEntity entity, ImpunityCallback<EntitySyncReport>? onComplete)
		{
			if (entity == null) throw new ArgumentNullException(nameof(entity));
			if (Connection == null || entity.Manager != this || FindEntity(entity.DistributedEntityId) != entity)
			{
				onComplete?.Invoke(new ImpunityErrorResponse(ImpunityErrorCode.ActionInvalidParameter,
					"Entity " + entity.DistributedEntityId + " isn't registered with a connected manager"), null!);
				return;
			}

			Connection.GetEntitySnapshot(entity.DistributedEntityId, (error, snapshot) =>
			{
				if (error != null)
				{
					onComplete?.Invoke(error, null!);
					return;
				}
				if (FindEntity(entity.DistributedEntityId) != entity)
				{
					onComplete?.Invoke(new ImpunityErrorResponse(ImpunityErrorCode.ActionNotFound,
						"Entity " + entity.DistributedEntityId + " was unregistered before the snapshot arrived"), null!);
					return;
				}

				EntitySyncReport report;
				try
				{
					report = CompareWithSnapshot(entity, snapshot);
				}
				catch (Exception e)
				{
					onComplete?.Invoke(new ImpunityErrorResponse(ImpunityErrorCode.ClientMappingError,
						"Couldn't compare entity " + entity.DistributedEntityId + " with its snapshot: " + e.Message), null!);
					return;
				}
				onComplete?.Invoke(null, report);
			});
		}

		static FieldSyncState Classify(IDistributedEntity entity, DistributedTypeFieldInfo fieldInfo, FieldSyncResult result,
			uint snapshotSeq, uint newestOwnWrite)
		{
			if (result.Differences.Count == 0)
			{
				return FieldSyncState.Match;
			}

			// Written here and not sent yet.
			if ((entity.DirtyBits & fieldInfo.FieldBitmask) != 0)
			{
				return FieldSyncState.InFlightToServer;
			}

			// IsClientAuthoritative is only true on the owner (receivers aren't told), whose writes are never echoed, so
			// its received seqs don't move when it writes. Its updates carry its own SendSeq, which the server records per
			// field: if the newest of this client's updates the server had applied is older than its latest send, an
			// update was still on its way when the snapshot was taken.
			if (entity.IsClientAuthoritative)
			{
				return ImpunityUtil.SeqIsNewer(entity.SendSeq, newestOwnWrite) ? FieldSyncState.InFlightToServer : FieldSyncState.Mismatch;
			}

			if (ImpunityUtil.SeqIsNewer(result.ServerSeq, result.ClientSeq))
			{
				return FieldSyncState.InFlightToClient;
			}

			if (ImpunityUtil.SeqIsNewer(result.ClientSeq, snapshotSeq))
			{
				return FieldSyncState.ChangedSinceSnapshot;
			}

			return FieldSyncState.Mismatch;
		}

		BsonValue ReadFieldAsBson(IDistributedEntity entity, DistributedTypeFieldInfo fieldInfo)
		{
			return (BsonValue?)fieldInfo.GetAsBsonMethod.Invoke(entity, GetAsBsonMethodArgs) ?? BsonValue.Null;
		}
	}
}
