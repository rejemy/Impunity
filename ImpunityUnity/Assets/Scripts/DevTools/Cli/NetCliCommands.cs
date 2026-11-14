using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using UnityEngine;

using Impunity.Connection;
using Unity.Pipeline.Commands;

using DevTools;

namespace DevTools.Cli
{
	// `dev net_*`: replicated-state inspection as JSON, for tools and agents. The JSON form of the
	// `net channels/channel/entity/sync` and `net server …` console verbs; both sit on NetInspector
	// (docs/AgentTooling.md, "Inspecting replicated state"). Server queries are a round trip, so
	// every command here is async.
	internal static class NetCliCommands
	{
		[CliGroupCommand("dev", "replicated channels. This client's subscriptions [{id, name, type, objects, persisted, locked}], or with --server true every channel in the server's live memory [{id, name, type, objects, persisted, subscribers, idleMs, lockedBy}] (--prefix narrows by name).",
			Name = "net_channels", Tags = new[] { "devtools" }, Effect = CommandEffect.ReadOnly)]
		private static Task<ExitCode> Channels(CommandContext context,
			[CliArg("server", "Ask the server instead of listing this client's subscriptions")] bool server = false,
			[CliArg("prefix", "With --server: only channels whose name starts with this")] string prefix = null)
		{
			return Run(context, async () => server
				? (object)await NetInspector.ServerChannels(prefix)
				: NetInspector.LocalChannels());
		}

		[CliGroupCommand("dev", "one channel and its objects [{id, type, name, label, …}]. This client's copy (label = what the game calls it: def @tile, player …; dirty = unsent fields), or with --server true the server's (lockedBy, lockWaiters, owner, seq; here = whether this client has it).",
			Name = "net_channel", Tags = new[] { "devtools" }, Effect = CommandEffect.ReadOnly)]
		private static Task<ExitCode> Channel(CommandContext context,
			[CliArg("name", "Channel name", Required = true)] string name,
			[CliArg("server", "Ask the server instead of this client")] bool server = false)
		{
			return Run(context, async () =>
			{
				if (server)
					return await NetInspector.ServerChannel(name);
				return NetInspector.LocalChannel(name)
					?? throw new InvalidOperationException("not subscribed to '" + name + "' (try --server true)");
			});
		}

		[CliGroupCommand("dev", "one entity's replicated state, every field by name as JSON. entity = an id, 'me', a channel name or an object's unique name. With --server true, the server's copy decoded with this client's types plus fieldInfo [{name, seq, ageMs, writer}].",
			Name = "net_entity", Tags = new[] { "devtools" }, Effect = CommandEffect.ReadOnly)]
		private static Task<ExitCode> Entity(CommandContext context,
			[CliArg("entity", "An id, 'me', a channel name or an object's unique name", Required = true)] string entity,
			[CliArg("server", "Ask the server for its copy")] bool server = false)
		{
			return Run(context, async () =>
			{
				IDistributedEntity local = null;
				if (!uint.TryParse(entity, out uint id))
				{
					local = NetInspector.Resolve(entity, out string error) ?? throw new InvalidOperationException(error);
					id = local.DistributedEntityId;
				}
				if (server)
					return await NetInspector.ServerEntity(id);
				local ??= NetInspector.Resolve(entity, out string notHere) ?? throw new InvalidOperationException(notHere);
				return NetInspector.LocalEntity(local);
			});
		}

		[CliGroupCommand("dev", "compare this client's replicated state with the server's. {checked, mismatched, inFlight, errors, entities:[{id, type, label, inSync, fields:[{name, state, clientSeq, serverSeq, differences}]}]} — only entities/fields that differ unless --full true. state Mismatch = a desync; InFlightToClient/InFlightToServer/ChangedSinceSnapshot = a change on its way (check again). Exits non-zero when anything is out of sync.",
			Name = "net_sync", Tags = new[] { "devtools" }, Effect = CommandEffect.ReadOnly)]
		private static async Task<ExitCode> Sync(CommandContext context,
			[CliArg("target", "'all' (default), a channel name (it and its objects), or one entity")] string target = "all",
			[CliArg("full", "List matching entities and fields too")] bool full = false)
		{
			bool outOfSync = false;
			ExitCode code = await Run(context, async () =>
			{
				List<IDistributedEntity> targets = NetInspector.SyncTargets(target, out string error)
					?? throw new InvalidOperationException(error);
				NetInspector.SyncResult sync = await NetInspector.Sync(targets);
				outOfSync = sync.Mismatched > 0;
				return NetInspector.SyncPlain(sync, full);
			});
			if (code == ExitCode.Success && outOfSync)
			{
				context.Log.Error("replicated state is out of sync (see mismatched)");
				return ExitCode.Failed;
			}
			return code;
		}

		[CliGroupCommand("dev", "the server's connections [{id, self, remote, udp, subscriptions, locks, ephemerals}].",
			Name = "net_conns", Tags = new[] { "devtools" }, Effect = CommandEffect.ReadOnly)]
		private static Task<ExitCode> Connections(CommandContext context)
		{
			return Run(context, async () => await NetInspector.ServerConnections());
		}

		// Runs a query on the main thread and writes its result; a failure is the error message
		private static async Task<ExitCode> Run(CommandContext context, Func<Task<object>> query)
		{
			if (!Application.isPlaying)
			{
				context.Log.Error("the game is not running (enter play mode first: tools/ucmd play)");
				return ExitCode.Failed;
			}
			string why = NetInspector.Unavailable();
			if (why != null)
			{
				context.Log.Error(why);
				return ExitCode.Failed;
			}
			try
			{
				context.Result.Write(await query());
				return ExitCode.Success;
			}
			catch (Exception e)
			{
				context.Log.Error(e.Message);
				return ExitCode.Failed;
			}
		}
	}
}
