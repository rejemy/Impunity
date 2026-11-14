using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using UnityEngine;

using Impunity.Connection;
using Impunity.GameState;
using UltraLiteDB;

namespace DevTools
{
	// Replicated-state inspection for the `net` console verbs and the `dev net_*` Pipeline commands
	// (docs/AgentTooling.md, "Inspecting replicated state"). Two views of the same state:
	//   local  — this client's copies (what its views draw from), straight from the entity manager;
	//   server — the server's live memory, through Impunity's inspection queries (a round trip).
	// `Sync` compares them per field, classifying differences by sequence numbers so a change in
	// flight isn't reported as a desync (Impunity's ClientEntityManager.CheckSync).
	//
	// Everything returns plain objects (dictionaries, lists, primitives) that the Pipeline's JSON
	// writer and the console formatter both take. Main thread only.
	public static class NetInspector
	{
		// ───────── Access ─────────

		// ───────── Game hooks ─────────
		// The game tells the inspector what to look at. Only ConnectionSource is required; the rest
		// make references and labels friendlier. Set them from the game's startup code.

		// The game's connection to its Impunity server (local or remote); null = nothing to inspect
		public static Func<BaseGameConnection> ConnectionSource;
		// What the reference "me" means: usually the first local player's entity
		public static Func<IDistributedEntity> Me;
		// The channel to prefer when several hold an object of the same unique name
		public static Func<string> CurrentChannel;
		// What the game calls an entity ("tree @12,7", "player Bob"); null = no label
		public static Func<IDistributedEntity, string> Labeler;
		// Replicated fields every client deliberately writes locally (SetLocalOnly) as a projection
		// of other replicated state, so their copies differ from the server's by design (e.g. a
		// walking character's Position following its Motion). A sync check reports them as
		// LocalProjection instead of a desync; keep it to fields that really are projections.
		public static Func<IDistributedEntity, string, bool> LocalProjection;

		public static BaseGameConnection Connection => ConnectionSource?.Invoke();
		public static ClientEntityManager Manager => Connection?.EntityManager;

		// Null when there's a connected game to inspect; otherwise why not
		public static string Unavailable()
		{
			if (ConnectionSource == null)
				return "the game hasn't told the inspector about its connection (set NetInspector.ConnectionSource)";
			BaseGameConnection connection = Connection;
			if (connection == null)
				return "no game connection yet";
			if (!connection.Connected)
				return "not connected";
			return null;
		}

		// An entity this client has, from a reference: "me" (whatever NetInspector.Me says),
		// an entity id, a subscribed channel's name, or an object's unique name — the
		// current channel's (CurrentChannel) first when several channels have one by that name.
		public static IDistributedEntity Resolve(string reference, out string error)
		{
			error = null;
			ClientEntityManager manager = Manager;
			if (manager == null)
			{
				error = Unavailable();
				return null;
			}
			reference = reference?.Trim();
			if (string.IsNullOrEmpty(reference))
			{
				error = "need an entity: an id, 'me', a channel name or an object's unique name";
				return null;
			}

			if (reference == "me")
			{
				IDistributedEntity me = Me?.Invoke();
				if (me == null || me.DistributedEntityId == 0)
					error = Me == null ? "'me' isn't set up (NetInspector.Me)" : "no 'me' entity right now";
				return error == null ? me : null;
			}

			if (uint.TryParse(reference, out uint id))
			{
				IDistributedEntity byId = manager.FindEntity(id);
				if (byId == null)
					error = "this client has no entity " + id + " (try 'net server entity " + id + "')";
				return byId;
			}

			foreach (IDistributedChannel channel in manager.CurrentSubscriptions)
			{
				if (channel.Name == reference)
					return channel;
			}

			List<IDistributedObject> named = new List<IDistributedObject>();
			foreach (IDistributedEntity entity in manager.GetRegisteredEntities())
			{
				if (entity is IDistributedObject obj && obj.UniqueName == reference)
					named.Add(obj);
			}
			if (named.Count == 1)
				return named[0];
			if (named.Count > 1)
			{
				string here = CurrentChannel?.Invoke();
				IDistributedObject inArea = named.FirstOrDefault(o => (o.Channel as IDistributedChannel)?.Name == here);
				if (inArea != null)
					return inArea;
				error = "'" + reference + "' names " + named.Count + " objects: "
					+ string.Join(", ", named.Select(o => o.DistributedEntityId + " in " + o.Channel?.Name));
				return null;
			}

			error = "no entity '" + reference + "' here (an id, 'me', a subscribed channel's name, or an object's unique name)";
			return null;
		}

		// ───────── Descriptions ─────────

		// What the game calls an entity (NetInspector.Labeler); a channel is its name
		public static string Label(IDistributedEntity entity)
		{
			if (entity is IDistributedChannel channel)
				return channel.Name;
			try
			{
				return Labeler?.Invoke(entity);
			}
			catch (Exception e)
			{
				return "(label failed: " + e.Message + ")";
			}
		}

		public static string TypeName(IDistributedEntity entity) => entity.GetType().Name;

		public static List<Dictionary<string, object>> LocalChannels()
		{
			var result = new List<Dictionary<string, object>>();
			foreach (IDistributedChannel channel in Manager.CurrentSubscriptions.OrderBy(c => c.Name, StringComparer.Ordinal))
			{
				result.Add(new Dictionary<string, object>
				{
					["id"] = channel.DistributedEntityId,
					["name"] = channel.Name,
					["type"] = TypeName(channel),
					["objects"] = (channel as DistributedChannelBase)?.DistributedObjects.Count ?? 0,
					["persisted"] = channel.IsPersisted,
					["locked"] = channel.IsLocked,
				});
			}
			return result;
		}

		// One line's worth per entity: id, type, unique name, label, lock and pending-write state
		public static Dictionary<string, object> LocalSummary(IDistributedEntity entity)
		{
			var summary = new Dictionary<string, object>
			{
				["id"] = entity.DistributedEntityId,
				["type"] = TypeName(entity),
			};
			if (entity is IDistributedObject obj)
			{
				summary["name"] = obj.UniqueName;
				summary["channel"] = (obj.Channel as IDistributedChannel)?.Name;
			}
			summary["label"] = Label(entity);
			summary["locked"] = entity.IsLocked;
			summary["persisted"] = entity.IsPersisted;
			summary["clientAuthoritative"] = entity.IsClientAuthoritative;
			summary["dirty"] = DirtyFieldNames(entity);
			return summary;
		}

		// A subscribed channel and its objects as this client has them; null when not subscribed
		public static Dictionary<string, object> LocalChannel(string name)
		{
			IDistributedChannel channel = Manager.CurrentSubscriptions.FirstOrDefault(c => c.Name == name);
			if (channel == null)
				return null;
			var objects = new List<Dictionary<string, object>>();
			if (channel is DistributedChannelBase withObjects)
			{
				foreach (IDistributedObject obj in withObjects.DistributedObjects.Values.OrderBy(o => o.DistributedEntityId))
					objects.Add(LocalSummary(obj));
			}
			Dictionary<string, object> result = LocalSummary(channel);
			result["objects"] = objects;
			return result;
		}

		// An entity's summary plus every replicated field, by name, as this client holds it
		public static Dictionary<string, object> LocalEntity(IDistributedEntity entity)
		{
			Dictionary<string, object> result = LocalSummary(entity);
			result["fields"] = ToPlain(Manager.GetFieldsAsBson(entity));
			return result;
		}

		public static async Task<List<Dictionary<string, object>>> ServerChannels(string prefix)
		{
			List<ChannelInfo> channels = await Connection.DescribeChannels(prefix);
			return channels.Select(ServerChannelSummary).ToList();
		}

		public static async Task<Dictionary<string, object>> ServerChannel(string name)
		{
			ChannelContents contents = await Connection.DescribeChannel(name);
			Dictionary<string, object> result = ServerChannelSummary(contents.Channel);
			result["objects"] = contents.Objects.Select(ServerEntitySummary).ToList();
			return result;
		}

		public static async Task<Dictionary<string, object>> ServerEntity(uint id)
		{
			return ServerEntity(await Connection.GetEntitySnapshot(id));
		}

		// A server snapshot's summary plus every field, decoded with this client's types, and when
		// and by whom each last changed
		public static Dictionary<string, object> ServerEntity(EntitySnapshot snapshot)
		{
			Dictionary<string, object> result = ServerEntitySummary(snapshot.Info);
			result["channel"] = snapshot.ChannelName;
			result["fields"] = ToPlain(Manager.DecodeSnapshot(snapshot));
			result["fieldInfo"] = snapshot.Fields.Select(f => new Dictionary<string, object>
			{
				["name"] = f.Name,
				["seq"] = f.LastModSeq,
				["ageMs"] = f.ModifiedMs == 0 ? (long?)null : snapshot.ServerTimeMs - f.ModifiedMs,
				["writer"] = f.LastWriter,
			}).ToList();
			return result;
		}

		public static async Task<List<Dictionary<string, object>>> ServerConnections()
		{
			List<ConnectionInfo> connections = await Connection.DescribeConnections();
			string me = Connection.ConnectionId;
			return connections.Select(c => new Dictionary<string, object>
			{
				["id"] = c.Id,
				["self"] = c.Id == me,
				["remote"] = c.IsRemote,
				["udp"] = c.SupportsUnguaranteed,
				["subscriptions"] = c.Subscriptions,
				["locks"] = c.LocksHeld,
				["ephemerals"] = c.Ephemerals,
			}).ToList();
		}

		static Dictionary<string, object> ServerChannelSummary(ChannelInfo c)
		{
			return new Dictionary<string, object>
			{
				["id"] = c.Id,
				["name"] = c.Name,
				["type"] = c.TypeName,
				["objects"] = c.ObjectCount,
				["persisted"] = c.IsPersisted,
				["subscribers"] = c.Subscribers,
				["idleMs"] = c.IdleSinceMs == 0 ? (long?)null : Connection.GetServerTime() - c.IdleSinceMs,
				["lockedBy"] = c.LockedBy,
			};
		}

		static Dictionary<string, object> ServerEntitySummary(EntityInfo e)
		{
			IDistributedEntity local = Manager.FindEntity(e.Id);
			return new Dictionary<string, object>
			{
				["id"] = e.Id,
				["type"] = e.TypeName,
				["name"] = e.Name,
				["label"] = local != null ? Label(local) : null,
				["here"] = local != null,
				["persisted"] = e.IsPersisted,
				["clientAuthoritative"] = e.IsClientAuthoritative,
				["lockedBy"] = e.LockedBy,
				["lockWaiters"] = e.LockWaiters,
				["owner"] = e.EphemeralOwner,
				["seq"] = e.Seq,
			};
		}

		static List<string> DirtyFieldNames(IDistributedEntity entity)
		{
			var names = new List<string>();
			if (entity.DirtyBits == 0)
				return names;
			foreach (DistributedFieldInfo field in Manager.GetFieldSchema(entity.GetType()))
			{
				if ((entity.DirtyBits & (1UL << (field.FieldId - 1))) != 0)
					names.Add(field.FieldName);
			}
			return names;
		}

		// ───────── Sync checks ─────────

		public sealed class SyncResult
		{
			public readonly List<EntitySyncReport> Reports = new List<EntitySyncReport>();
			public readonly List<string> Errors = new List<string>();
			public int Checked => Reports.Count;
			public int Mismatched => Reports.Count(OutOfSync);
			public int Unsettled => Reports.Count(r => !OutOfSync(r) && HasNoteworthy(r));
		}

		// See LocalProjection (the hook)
		static bool IsLocalProjection(IDistributedEntity entity, string field)
		{
			return LocalProjection != null && LocalProjection(entity, field);
		}

		// A field's state as reported: Impunity's FieldSyncState, or LocalProjection
		public static string StateName(IDistributedEntity entity, FieldSyncResult field)
		{
			return field.State == FieldSyncState.Mismatch && IsLocalProjection(entity, field.Name)
				? "LocalProjection"
				: field.State.ToString();
		}

		public static bool OutOfSync(EntitySyncReport report)
		{
			return report.Fields.Any(f => f.State == FieldSyncState.Mismatch && !IsLocalProjection(report.Entity, f.Name));
		}

		// Worth listing without 'full': anything but a match or a known local projection
		public static bool IsNoteworthy(EntitySyncReport report, FieldSyncResult field)
		{
			return field.State != FieldSyncState.Match
				&& !(field.State == FieldSyncState.Mismatch && IsLocalProjection(report.Entity, field.Name));
		}

		public static bool HasNoteworthy(EntitySyncReport report) => report.Fields.Any(f => IsNoteworthy(report, f));

		// What `net sync <target>` checks: one entity, a subscribed channel and its objects, or
		// (target "all") everything this client has
		public static List<IDistributedEntity> SyncTargets(string target, out string error)
		{
			error = null;
			if (string.IsNullOrEmpty(target) || target == "all")
				return Manager.GetRegisteredEntities().ToList();

			IDistributedEntity entity = Resolve(target, out error);
			if (entity == null)
				return null;
			var targets = new List<IDistributedEntity> { entity };
			if (entity is DistributedChannelBase channel)
				targets.AddRange(channel.DistributedObjects.Values.OrderBy(o => o.DistributedEntityId));
			return targets;
		}

		// Checks every target against a fresh server snapshot. The requests go out together, so
		// a big channel costs one round trip, not one per object.
		public static async Task<SyncResult> Sync(List<IDistributedEntity> targets)
		{
			ClientEntityManager manager = Manager;
			var checks = targets.Select(e => (entity: e, task: manager.CheckSyncAsync(e))).ToList();
			var result = new SyncResult();
			foreach (var (entity, task) in checks)
			{
				try
				{
					result.Reports.Add(await task);
				}
				catch (Exception e)
				{
					result.Errors.Add(entity.DistributedEntityId + " (" + TypeName(entity) + "): " + e.Message);
				}
			}
			return result;
		}

		// A sync result as plain objects. Matching fields are left out unless asked for: a report
		// lists only the entities and fields that differ, with what differs.
		public static Dictionary<string, object> SyncPlain(SyncResult sync, bool includeSettled)
		{
			var entities = new List<Dictionary<string, object>>();
			foreach (EntitySyncReport report in sync.Reports)
			{
				if (!includeSettled && !HasNoteworthy(report))
					continue;
				entities.Add(new Dictionary<string, object>
				{
					["id"] = report.Entity.DistributedEntityId,
					["type"] = TypeName(report.Entity),
					["label"] = Label(report.Entity),
					["inSync"] = !OutOfSync(report),
					["fields"] = report.Fields
						.Where(f => includeSettled || IsNoteworthy(report, f))
						.Select(f => new Dictionary<string, object>
						{
							["name"] = f.Name,
							["state"] = StateName(report.Entity, f),
							["clientSeq"] = f.ClientSeq,
							["serverSeq"] = f.ServerSeq,
							["differences"] = f.Differences,
						}).ToList(),
				});
			}
			return new Dictionary<string, object>
			{
				["checked"] = sync.Checked,
				["mismatched"] = sync.Mismatched,
				["inFlight"] = sync.Unsettled,
				["errors"] = sync.Errors,
				["entities"] = entities,
			};
		}

		// ───────── BSON → plain ─────────

		// BSON as dictionaries/lists/primitives for a JSON writer; binary as base64
		public static object ToPlain(BsonValue value)
		{
			if (value == null || value.IsNull)
				return null;
			switch (value.Type)
			{
				case BsonType.Document:
					var doc = new Dictionary<string, object>();
					foreach (var pair in value.AsDocument)
						doc[pair.Key] = ToPlain(pair.Value);
					return doc;
				case BsonType.Array:
					return value.AsArray.Select(ToPlain).ToList();
				case BsonType.Binary:
					var bytes = value.AsBinary;
					return Convert.ToBase64String(bytes.Array, bytes.Offset, bytes.Count);
				case BsonType.ObjectId:
				case BsonType.Guid:
				case BsonType.DateTime:
					return value.AsRawValue?.ToString();
				case BsonType.MinValue:
				case BsonType.MaxValue:
					return value.Type.ToString();
				default:
					return value.AsRawValue;
			}
		}

		// ───────── Text (console) ─────────

		// A value on one console line: compact JSON, cut at maxChars (0 = whole)
		public static string Json(BsonValue value, int maxChars)
		{
			string json = value == null ? "null" : JsonSerializer.Serialize(value);
			return maxChars > 0 && json.Length > maxChars ? json.Substring(0, maxChars - 3) + "..." : json;
		}

		public static string Flags(Dictionary<string, object> e)
		{
			var flags = new List<string>();
			if (e.TryGetValue("persisted", out object p) && p is bool persisted && persisted) flags.Add("persisted");
			if (e.TryGetValue("clientAuthoritative", out object ca) && ca is bool isCa && isCa) flags.Add("client-auth");
			if (e.TryGetValue("locked", out object l) && l is bool locked && locked) flags.Add("locked");
			if (e.TryGetValue("lockedBy", out object lb) && lb != null) flags.Add("locked by " + lb);
			if (e.TryGetValue("lockWaiters", out object lw) && lw is int waiters && waiters > 0) flags.Add(waiters + " waiting");
			if (e.TryGetValue("owner", out object o) && o != null) flags.Add("owned by " + o);
			if (e.TryGetValue("dirty", out object d) && d is List<string> dirty && dirty.Count > 0) flags.Add("dirty " + string.Join(",", dirty));
			return flags.Count == 0 ? "" : "  [" + string.Join(", ", flags) + "]";
		}

		public static string EntityLine(Dictionary<string, object> e)
		{
			string name = e.TryGetValue("name", out object n) && n != null ? " '" + n + "'" : "";
			string label = e.TryGetValue("label", out object l) && l != null && !Equals(l, n) ? "  " + l : "";
			return "  " + e["id"] + "  " + e["type"] + name + label + Flags(e);
		}
	}
}
