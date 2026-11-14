using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using UnityEngine;

using Impunity.Connection;
using Impunity.GameState;
using UltraLiteDB;

using Dreamwing.Cons;



namespace DevTools
{
	// `net ...`: inspecting replicated state (this client's copies, the server's, and whether they
	// agree), through NetInspector. A game adds its own verbs (host, join, …) to the same group.
	public static class NetCommands
	{
		public static void Register()
		{
			Cons.AddCommandGroup("net", "Replicated state: this client's copies, the server's, and whether they agree");

			Cons.AddCommand("net channels", Channels,
				"Lists the channels this client subscribes to: id, type, object count. The server's view of every live channel is 'net server channels'");

			Cons.AddCommand<string>("net channel", Channel,
				"Lists a subscribed channel's objects as this client has them: id, type, unique name, what the game calls it (def @tile, player …), lock and unsent (dirty) fields",
				new CommandStringArgument("name") { Completer = ChannelNames });

			Cons.AddCommand<string, string>("net entity", Entity,
				"Shows an entity's replicated fields as this client holds them, one per line as JSON (each cut at 200 characters unless the field is named). entity = an id, 'me', a channel name or an object's unique name (go_x_y)",
				new CommandStringArgument("entity"),
				new CommandStringArgument("field", ""));

			Cons.AddCommand<string, string>("net sync", Sync,
				"Compares this client's copies with the server's: target = 'all' (everything this client has), a channel (and its objects) or one entity. Reports each differing field as a Mismatch (a desync) or a change in flight (InFlightToClient/ToServer, ChangedSinceSnapshot — check again). 'full' lists matching fields, and LocalProjection ones (fields every client writes locally by design, like a walking character's Position), too",
				new CommandStringArgument("target", "all"),
				new CommandStringArgument("detail", "") { PossibleValues = new[] { "", "full" } });

			Cons.AddCommandGroup("net server", "The server's live state, asked over the connection (works on the host and on a joined client)");

			Cons.AddCommand<string>("net server channels", ServerChannels,
				"Lists every channel in the server's live memory: id, type, objects, subscribers (connection ids), idle time, lock holder; prefix narrows by name",
				new CommandStringArgument("prefix", ""));

			Cons.AddCommand<string>("net server channel", ServerChannel,
				"Lists a live channel's objects on the server, subscribed or not: id, type, unique name, lock holder and waiters, owner, broadcast seq",
				new CommandStringArgument("name") { Completer = ChannelNames });

			Cons.AddCommand<string, string>("net server entity", ServerEntity,
				"Shows the server's copy of an entity: every field decoded with this client's types, each with its last change (seq, age, writer). entity = an id, or anything 'net entity' takes that this client has",
				new CommandStringArgument("entity"),
				new CommandStringArgument("field", ""));

			Cons.AddCommand("net server conns", ServerConnections,
				"Lists the server's connections: id (yours marked), local or remote, UDP, subscriptions, locks held, owned delete-on-disconnect entities");

		}

		// ───────── Replicated-state inspection (NetInspector) ─────────

		private static IEnumerable<string> ChannelNames()
		{
			return NetInspector.Manager?.CurrentSubscriptions.Select(c => c.Name) ?? Enumerable.Empty<string>();
		}

		private static bool Ready(string cmd)
		{
			string why = NetInspector.Unavailable();
			if (why != null)
				Cons.LogError(cmd + ": " + why);
			return why == null;
		}

		private static void Channels()
		{
			if (!Ready("net channels")) return;
			var channels = NetInspector.LocalChannels();
			Cons.Log("net channels: " + channels.Count + " subscribed");
			foreach (var c in channels)
				Cons.Log("  " + c["id"] + "  " + c["name"] + "  (" + c["type"] + ")  " + c["objects"] + " objects" + NetInspector.Flags(c));
		}

		private static void Channel(string name)
		{
			if (!Ready("net channel")) return;
			var channel = NetInspector.LocalChannel(name);
			if (channel == null)
			{
				Cons.LogError("net channel: not subscribed to '" + name + "' (see 'net channels', or 'net server channel " + name + "')");
				return;
			}
			var objects = (List<Dictionary<string, object>>)channel["objects"];
			Cons.Log("net channel: " + name + " (" + channel["type"] + ", id " + channel["id"] + ") — " + objects.Count + " objects" + NetInspector.Flags(channel));
			foreach (var o in objects)
				Cons.Log(NetInspector.EntityLine(o));
		}

		private static void Entity(string reference, string field)
		{
			if (!Ready("net entity")) return;
			IDistributedEntity entity = NetInspector.Resolve(reference, out string error);
			if (entity == null)
			{
				Cons.LogError("net entity: " + error);
				return;
			}
			Cons.Log("net entity:" + NetInspector.EntityLine(NetInspector.LocalSummary(entity)));
			LogFields("net entity", NetInspector.Manager.GetFieldsAsBson(entity), field, Cons.Log, Cons.LogError);
		}

		private static void LogFields(string cmd, BsonDocument fields, string only, Action<string> log, Action<string> logError)
		{
			if (!string.IsNullOrEmpty(only))
			{
				if (!fields.ContainsKey(only))
				{
					logError(cmd + ": no field '" + only + "' (has " + string.Join(", ", fields.Keys) + ")");
					return;
				}
				log("  " + only + " = " + NetInspector.Json(fields[only], 0));
				return;
			}
			foreach (var pair in fields)
				log("  " + pair.Key + " = " + NetInspector.Json(pair.Value, 200));
		}

		private static void Sync(string target, string detail)
		{
			if (!Ready("net sync")) return;
			List<IDistributedEntity> targets = NetInspector.SyncTargets(target, out string error);
			if (targets == null)
			{
				Cons.LogError("net sync: " + error);
				return;
			}
			ConsoleReply reply = Cons.Defer();
			RunSync(reply, targets, detail == "full");
		}

		private static async void RunSync(ConsoleReply reply, List<IDistributedEntity> targets, bool full)
		{
			try
			{
				NetInspector.SyncResult sync = await NetInspector.Sync(targets);
				reply.Log("net sync: checked " + sync.Checked + " entities — " + sync.Mismatched + " out of sync, "
					+ sync.Unsettled + " with changes in flight" + (sync.Errors.Count > 0 ? ", " + sync.Errors.Count + " couldn't be checked" : ""));
				foreach (EntitySyncReport report in sync.Reports)
				{
					if (!full && !NetInspector.HasNoteworthy(report))
						continue;
					bool outOfSync = NetInspector.OutOfSync(report);
					string label = NetInspector.Label(report.Entity);
					reply.Log("  " + report.Entity.DistributedEntityId + "  " + NetInspector.TypeName(report.Entity)
						+ (label != null ? "  " + label : "") + (outOfSync ? "  OUT OF SYNC" : ""));
					foreach (FieldSyncResult field in report.Fields)
					{
						if (!full && !NetInspector.IsNoteworthy(report, field))
							continue;
						string state = NetInspector.StateName(report.Entity, field);
						string line = "    " + field.Name + ": " + state + " (seq client " + field.ClientSeq + ", server " + field.ServerSeq + ")";
						if (state == nameof(FieldSyncState.Mismatch))
							reply.LogWarning(line);
						else
							reply.Log(line);
						foreach (string difference in field.Differences)
							reply.Log("      " + difference);
					}
				}
				foreach (string e in sync.Errors)
					reply.LogWarning("  couldn't check " + e);
				reply.Finish();
			}
			catch (Exception e)
			{
				reply.Fail("net sync: " + e.Message);
			}
		}

		private static void ServerChannels(string prefix)
		{
			if (!Ready("net server channels")) return;
			Ask("net server channels", async reply =>
			{
				var channels = await NetInspector.ServerChannels(prefix);
				reply.Log("net server channels: " + channels.Count + " live" + (string.IsNullOrEmpty(prefix) ? "" : " starting '" + prefix + "'"));
				foreach (var c in channels)
				{
					var subscribers = (List<string>)c["subscribers"];
					string idle = c["idleMs"] is long ms ? "  idle " + (ms / 1000) + "s" : "";
					reply.Log("  " + c["id"] + "  " + c["name"] + "  (" + c["type"] + ")  " + c["objects"] + " objects, "
						+ subscribers.Count + " subscribed" + (subscribers.Count > 0 ? " [" + string.Join(", ", subscribers) + "]" : "")
						+ idle + NetInspector.Flags(c));
				}
			});
		}

		private static void ServerChannel(string name)
		{
			if (!Ready("net server channel")) return;
			Ask("net server channel", async reply =>
			{
				var channel = await NetInspector.ServerChannel(name);
				var objects = (List<Dictionary<string, object>>)channel["objects"];
				var subscribers = (List<string>)channel["subscribers"];
				reply.Log("net server channel: " + name + " (" + channel["type"] + ", id " + channel["id"] + ") — " + objects.Count
					+ " objects, subscribers [" + string.Join(", ", subscribers) + "]" + NetInspector.Flags(channel));
				foreach (var o in objects)
					reply.Log(NetInspector.EntityLine(o) + "  seq " + o["seq"] + ((bool)o["here"] ? "" : "  (not on this client)"));
			});
		}

		private static void ServerEntity(string reference, string field)
		{
			if (!Ready("net server entity")) return;
			uint id;
			if (!uint.TryParse(reference, out id))
			{
				IDistributedEntity local = NetInspector.Resolve(reference, out string error);
				if (local == null)
				{
					Cons.LogError("net server entity: " + error);
					return;
				}
				id = local.DistributedEntityId;
			}
			Ask("net server entity", async reply =>
			{
				EntitySnapshot snapshot = await NetInspector.Connection.GetEntitySnapshot(id);
				Dictionary<string, object> summary = NetInspector.ServerEntity(snapshot);
				reply.Log("net server entity:" + NetInspector.EntityLine(summary) + "  seq " + snapshot.Info.Seq + "  in " + snapshot.ChannelName);
				BsonDocument fields = NetInspector.Manager.DecodeSnapshot(snapshot);
				foreach (var pair in fields)
				{
					if (!string.IsNullOrEmpty(field) && pair.Key != field)
						continue;
					FieldSnapshot info = snapshot.Fields.FirstOrDefault(f => f.Name == pair.Key);
					string meta = info == null || info.LastModSeq == 0 && info.ModifiedMs == 0
						? "  (unchanged since load)"
						: "  (seq " + info.LastModSeq + ", " + ((snapshot.ServerTimeMs - info.ModifiedMs) / 1000f).ToString("0.0") + "s ago"
							+ (info.LastWriter != null ? " by " + info.LastWriter : "") + ")";
					reply.Log("  " + pair.Key + " = " + NetInspector.Json(pair.Value, string.IsNullOrEmpty(field) ? 200 : 0) + meta);
				}
				if (!string.IsNullOrEmpty(field) && !fields.ContainsKey(field))
					reply.LogError("net server entity: no field '" + field + "' (has " + string.Join(", ", fields.Keys) + ")");
			});
		}

		private static void ServerConnections()
		{
			if (!Ready("net server conns")) return;
			Ask("net server conns", async reply =>
			{
				var connections = await NetInspector.ServerConnections();
				reply.Log("net server conns: " + connections.Count);
				foreach (var c in connections)
				{
					var subscriptions = (List<string>)c["subscriptions"];
					var locks = (List<uint>)c["locks"];
					var ephemerals = (List<uint>)c["ephemerals"];
					reply.Log("  " + c["id"] + ((bool)c["self"] ? " (you)" : "") + "  " + ((bool)c["remote"] ? "remote" : "local")
						+ ((bool)c["udp"] ? "+udp" : "") + "  subscribes [" + string.Join(", ", subscriptions) + "]"
						+ (locks.Count > 0 ? "  locks [" + string.Join(", ", locks) + "]" : "")
						+ (ephemerals.Count > 0 ? "  owns [" + string.Join(", ", ephemerals) + "]" : ""));
				}
			});
		}

		// Runs a server query whose lines come back on a later frame (see Cons.Defer)
		private static async void Ask(string cmd, Func<ConsoleReply, Task> query)
		{
			ConsoleReply reply = Cons.Defer();
			try
			{
				await query(reply);
				reply.Finish();
			}
			catch (Exception e)
			{
				reply.Fail(cmd + ": " + e.Message);
			}
		}
	}
}
