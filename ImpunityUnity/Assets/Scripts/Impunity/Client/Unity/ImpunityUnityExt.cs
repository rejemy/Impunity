using System;
using System.Collections;
using System.Collections.Generic;

using UnityEngine;

using UltraLiteDB;

using Impunity.GameState;
using Impunity.Connection;


namespace Impunity.Unity
{

	/// <summary>
	/// Unity coroutine yield instruction that completes when an Impunity callback fires.
	/// Use with <c>yield return</c> in coroutines to wait for server operations.
	/// Check <see cref="Error"/> after yielding to detect failures.
	/// </summary>
	public class ImpunityYield : CustomYieldInstruction
	{
		private bool Complete = false;

		/// <summary>The error response if the operation failed, or null on success.</summary>
		public ImpunityErrorResponse? Error { get; private set; }

		public override bool keepWaiting
		{
			get
			{
				return !Complete;
			}
		}

		public void OnComplete(ImpunityErrorResponse? err)
		{
			Error = err;
			Complete = true;
		}
	}

	/// <summary>
	/// Generic version of <see cref="ImpunityYield"/> for operations that return a typed result.
	/// Access <see cref="Value"/> after yielding to get the result.
	/// </summary>
	/// <typeparam name="TReturn">The result type.</typeparam>
	public class ImpunityYield<TReturn> : CustomYieldInstruction
	{
		private bool Complete = false;
		/// <summary>The result value on success.</summary>
		public TReturn? Value { get; private set; }
		/// <summary>The error response if the operation failed, or null on success.</summary>
		public ImpunityErrorResponse? Error { get; private set; }

		public override bool keepWaiting
		{
			get
			{
				return !Complete;
			}
		}

		public void OnComplete(ImpunityErrorResponse? err, TReturn returnValue)
		{
			Value = returnValue;
			Error = err;
			Complete = true;
		}
	}

	/// <summary>
	/// Unity coroutine yield extension methods for <see cref="BaseGameConnection"/>. Each method wraps
	/// the corresponding callback-based API as an <see cref="ImpunityYield"/> for use with <c>yield return</c>.
	/// </summary>
	public static class ConnectionYieldExtensions
	{
		// ---------- API

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.Connect</c>. Yield it, then check <c>Error</c> (null on success).</summary>
		public static ImpunityYield ConnectYield(this BaseGameConnection connection)
		{
			var action = new ImpunityYield();
			connection.Connect(action.OnComplete);
			return action;
		}

		// -------- DB actions


		/// <summary>Coroutine wrapper for <c>BaseGameConnection.CompoundDatabaseAction</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<List<ActionResult>> CompoundDatabaseActionYield(this BaseGameConnection connection, IEnumerable<GameStateActionBase> actions)
		{
			var action = new ImpunityYield<List<ActionResult>>();
			connection.CompoundDatabaseAction(actions, action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.SetGameSummary</c>. Yield it, then check <c>Error</c> (null on success).</summary>
		public static ImpunityYield SetGameSummaryYield(this BaseGameConnection connection, BsonDocument summary)
		{
			var action = new ImpunityYield();
			connection.SetGameSummary(summary, action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.GetGameSummary</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<BsonDocument> GetGameSummaryYield(this BaseGameConnection connection)
		{
			var action = new ImpunityYield<BsonDocument>();
			connection.GetGameSummary(action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.InsertDocument</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<BsonValue> InsertDocumentYield(this BaseGameConnection connection, int collectionId, BsonDocument doc)
		{
			var action = new ImpunityYield<BsonValue>();
			connection.InsertDocument(collectionId, doc, action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.UpdateDocument</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<bool> UpdateDocumentYield(this BaseGameConnection connection, int collectionId, BsonDocument doc)
		{
			var action = new ImpunityYield<bool>();
			connection.UpdateDocument(collectionId, doc, action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.UpsertDocument</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<bool> UpsertDocumentYield(this BaseGameConnection connection, int collectionId, BsonDocument doc)
		{
			var action = new ImpunityYield<bool>();
			connection.UpsertDocument(collectionId, doc, action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.MergeIntoDocument</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<bool> MergeIntoDocumentYield(this BaseGameConnection connection, int collectionId, BsonDocument doc, IEnumerable<string>? unsetKeys = null)
		{
			var action = new ImpunityYield<bool>();
			connection.MergeIntoDocument(collectionId, doc, action.OnComplete, unsetKeys);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.MergeInsertDocument</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<bool> MergeInsertDocumentYield(this BaseGameConnection connection, int collectionId, BsonDocument doc, IEnumerable<string>? unsetKeys = null)
		{
			var action = new ImpunityYield<bool>();
			connection.MergeInsertDocument(collectionId, doc, action.OnComplete, unsetKeys);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.FindDocumentById</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<BsonDocument?> FindDocumentByIdYield(this BaseGameConnection connection, int collectionId, BsonValue id)
		{
			var action = new ImpunityYield<BsonDocument?>();
			connection.FindDocumentById(collectionId, id, action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.DeleteDocument</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<bool> DeleteDocumentYield(this BaseGameConnection connection, int collectionId, BsonValue id)
		{
			var action = new ImpunityYield<bool>();
			connection.DeleteDocument(collectionId, id, action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.ListDocuments</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<List<BsonDocument>> ListDocumentsYield(this BaseGameConnection connection, int collectionId)
		{
			var action = new ImpunityYield<List<BsonDocument>>();
			connection.ListDocuments(collectionId, action.OnComplete);
			return action;
		}

		// -------- Live game

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.TryToLock</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<bool> TryToLockYield(this BaseGameConnection connection, string lockName)
		{
			var action = new ImpunityYield<bool>();
			connection.TryToLock(lockName, action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.WaitForLock</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<LockWaitResult> WaitForLockYield(this BaseGameConnection connection, string lockName)
		{
			var action = new ImpunityYield<LockWaitResult>();
			connection.WaitForLock(lockName, action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.Unlock</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<bool> UnlockYield(this BaseGameConnection connection, string lockName)
		{
			var action = new ImpunityYield<bool>();
			connection.Unlock(lockName, action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.CreateChannel</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<bool> CreateChannelYield(this BaseGameConnection connection, string channelName, int entityTypeId, byte instanceFlags, ArraySegment<byte> propBytes, bool replace, IEnumerable<ObjectCreateData> channelObjects)
		{
			var action = new ImpunityYield<bool>();
			connection.CreateChannel(channelName, entityTypeId, instanceFlags, propBytes, replace, channelObjects, action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.SubcribeToChannel</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<uint> SubcribeToChannelYield(this BaseGameConnection connection, string channelName, bool createIfMissing, int entityTypeId, byte instanceFlags, ArraySegment<byte> propBytes, IEnumerable<ObjectCreateData> channelObjects)
		{
			var action = new ImpunityYield<uint>();
			connection.SubcribeToChannel(channelName, createIfMissing, entityTypeId, instanceFlags, propBytes, channelObjects, action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.UnsubscribeFromChannel</c>. Yield it, then check <c>Error</c> (null on success).</summary>
		public static ImpunityYield UnsubscribeFromChannelYield(this BaseGameConnection connection, uint channelId)
		{
			var action = new ImpunityYield();
			connection.UnsubscribeFromChannel(channelId, action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.CreateObject</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<uint> CreateObjectYield(this BaseGameConnection connection, int entityTypeId, byte instanceFlags, uint channelId, ArraySegment<byte> propBytes, string uniqueName, bool replace, GameStateActionBase? onCreatedAction = null, uint replaceEntityId = 0, BsonValue? replaceData = null)
		{
			var action = new ImpunityYield<uint>();
			connection.CreateObject(entityTypeId, instanceFlags, channelId, propBytes, uniqueName, replace, action.OnComplete, onCreatedAction, replaceEntityId, replaceData);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.UpdateEntity</c>. Yield it, then check <c>Error</c> (null on success).</summary>
		public static ImpunityYield UpdateEntityYield(this BaseGameConnection connection, uint entityId, ArraySegment<byte> updateData, bool guaranteed, uint seq = 0)
		{
			var action = new ImpunityYield();
			connection.UpdateEntity(entityId, updateData, guaranteed, seq, action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.DeleteEntity</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<bool> DeleteEntityYield(this BaseGameConnection connection, uint entityId, BsonValue deleteData, GameStateActionBase? onDeletedAction = null)
		{
			var action = new ImpunityYield<bool>();
			connection.DeleteEntity(entityId, deleteData, action.OnComplete, onDeletedAction);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.TriggerEntityEvent</c>. Yield it, then check <c>Error</c> (null on success).</summary>
		public static ImpunityYield TriggerEntityEventYield(this BaseGameConnection connection, uint entityId, int eventType, BsonValue eventData,
			EntityEventEcho echo = EntityEventEcho.IncludeSender)
		{
			var action = new ImpunityYield();
			connection.TriggerEntityEvent(entityId, eventType, eventData, echo, action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.TryToLockEntity</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<bool> TryToLockEntityYield(this BaseGameConnection connection, uint entityId)
		{
			var action = new ImpunityYield<bool>();
			connection.TryToLockEntity(entityId, action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.UnlockEntity</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<bool> UnlockEntityYield(this BaseGameConnection connection, uint entityId)
		{
			var action = new ImpunityYield<bool>();
			connection.UnlockEntity(entityId, action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.ListActiveChannels</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<List<string>> ListActiveChannelsYield(this BaseGameConnection connection)
		{
			var action = new ImpunityYield<List<string>>();
			connection.ListActiveChannels(action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.ListPersistedChannels</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<List<string>> ListPersistedChannelsYield(this BaseGameConnection connection)
		{
			var action = new ImpunityYield<List<string>>();
			connection.ListPersistedChannels(action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.DescribeChannels</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<List<ChannelInfo>> DescribeChannelsYield(this BaseGameConnection connection, string? namePrefix = null)
		{
			var action = new ImpunityYield<List<ChannelInfo>>();
			connection.DescribeChannels(namePrefix, action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.DescribeChannel</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<ChannelContents> DescribeChannelYield(this BaseGameConnection connection, string channelName)
		{
			var action = new ImpunityYield<ChannelContents>();
			connection.DescribeChannel(channelName, action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.GetEntitySnapshot</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<EntitySnapshot> GetEntitySnapshotYield(this BaseGameConnection connection, uint entityId)
		{
			var action = new ImpunityYield<EntitySnapshot>();
			connection.GetEntitySnapshot(entityId, action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.DescribeConnections</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<List<ConnectionInfo>> DescribeConnectionsYield(this BaseGameConnection connection)
		{
			var action = new ImpunityYield<List<ConnectionInfo>>();
			connection.DescribeConnections(action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>ClientEntityManager.CheckSync</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<EntitySyncReport> CheckSyncYield(this ClientEntityManager manager, IDistributedEntity entity)
		{
			var action = new ImpunityYield<EntitySyncReport>();
			manager.CheckSync(entity, action.OnComplete);
			return action;
		}
	}

	/// <summary>Unity coroutine yield extension methods for <see cref="GameStateDBCollection{DTYPE}"/>.</summary>
	public static class GameStateDBCollectionYieldExtensions
	{

		/// <summary>Coroutine wrapper for <c>GameStateDBCollection{DTYPE}.InsertDocument</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<BsonValue> InsertDocumentYield<DTYPE>(this GameStateDBCollection<DTYPE> collection, DTYPE doc)
		{
			ImpunityYield<BsonValue> action = new ImpunityYield<BsonValue>();
			collection.InsertDocument(doc, action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>GameStateDBCollection{DTYPE}.UpdateDocument</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<bool> UpdateDocumentYield<DTYPE>(this GameStateDBCollection<DTYPE> collection, DTYPE doc)
		{
			ImpunityYield<bool> action = new ImpunityYield<bool>();
			collection.UpdateDocument(doc, action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>GameStateDBCollection{DTYPE}.UpsertDocument</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<bool> UpsertDocumentYield<DTYPE>(this GameStateDBCollection<DTYPE> collection, DTYPE doc)
		{
			ImpunityYield<bool> action = new ImpunityYield<bool>();
			collection.UpsertDocument(doc, action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>GameStateDBCollection{DTYPE}.MergeIntoDocument</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<bool> MergeIntoDocumentYield<DTYPE>(this GameStateDBCollection<DTYPE> collection, BsonDocument patch, IEnumerable<string>? unsetKeys = null)
		{
			ImpunityYield<bool> action = new ImpunityYield<bool>();
			collection.MergeIntoDocument(patch, action.OnComplete, unsetKeys);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>GameStateDBCollection{DTYPE}.MergeInsertDocument</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<bool> MergeInsertDocumentYield<DTYPE>(this GameStateDBCollection<DTYPE> collection, BsonDocument patch, IEnumerable<string>? unsetKeys = null)
		{
			ImpunityYield<bool> action = new ImpunityYield<bool>();
			collection.MergeInsertDocument(patch, action.OnComplete, unsetKeys);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>GameStateDBCollection{DTYPE}.FindDocumentById</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<DTYPE?> FindDocumentByIdYield<DTYPE>(this GameStateDBCollection<DTYPE> collection, BsonValue id)
		{
			ImpunityYield<DTYPE?> action = new ImpunityYield<DTYPE?>();
			collection.FindDocumentById(id, action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>GameStateDBCollection{DTYPE}.DeleteDocument</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<bool> DeleteDocumentYield<DTYPE>(this GameStateDBCollection<DTYPE> collection, BsonValue id)
		{
			ImpunityYield<bool> action = new ImpunityYield<bool>();
			collection.DeleteDocument(id, action.OnComplete);
			return action;
		}

		/// <summary>Coroutine wrapper for <c>GameStateDBCollection{DTYPE}.ListDocuments</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<List<DTYPE>?> ListDocumentsYield<DTYPE>(this GameStateDBCollection<DTYPE> collection)
		{
			ImpunityYield<List<DTYPE>?> action = new ImpunityYield<List<DTYPE>?>();
			collection.ListDocuments(action.OnComplete);
			return action;
		}
	}

	/// <summary>Unity coroutine yield extension methods for <see cref="ClientEntityManager"/>.</summary>
	public static class ClientEntityManagerYieldExtensions
	{
		/// <summary>Coroutine wrapper for <c>ClientEntityManager.CreateObject</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<T> CreateObjectYield<T>(this ClientEntityManager manager, T obj, IDistributedChannel channel, bool replace, GameStateActionBase? onCreatedAction = null, uint replaceEntityId = 0, FieldGuard replaceGuard = default, BsonValue? replaceData = null) where T : class, IDistributedObject
		{
			var t = new ImpunityYield<T>();
			manager.CreateObject<T>(obj, channel, replace, t.OnComplete, onCreatedAction, replaceEntityId, replaceGuard, replaceData);
			return t;
		}

		/// <summary>Coroutine wrapper for <c>ClientEntityManager.CreateChannel</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<bool> CreateChannelYield<T>(this ClientEntityManager manager, string channelName, T channel, bool replace, IEnumerable<IDistributedObject> channelObjects) where T : class, IDistributedChannel
		{
			var t = new ImpunityYield<bool>();
			manager.CreateChannel<T>(channelName, channel, replace, channelObjects, t.OnComplete);
			return t;
		}

		/// <summary>Coroutine wrapper for <c>ClientEntityManager.SubscribeToChannel</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<T> SubscribeToChannelYield<T>(this ClientEntityManager manager, string channelName, T createIfNeeded) where T : class, IDistributedChannel
		{
			var t = new ImpunityYield<T>();
			manager.SubscribeToChannel<T>(channelName, createIfNeeded, t.OnComplete);
			return t;
		}

		/// <summary>Coroutine wrapper for <c>ClientEntityManager.UnsubscribeFromChannel</c>. Yield it, then check <c>Error</c> (null on success).</summary>
		public static ImpunityYield UnsubscribeFromChannelYield(this ClientEntityManager manager, IDistributedChannel channel, bool immediate = false)
		{
			var t = new ImpunityYield();
			manager.UnsubscribeFromChannel(channel, t.OnComplete, immediate);
			return t;
		}
	}

	/// <summary>Unity coroutine yield extension methods for <see cref="IDistributedEntity"/>.</summary>
	public static class IDistributedEntityYieldExtensions
	{
		/// <summary>Coroutine wrapper for <c>IDistributedEntity.TriggerEvent</c>. Yield it, then check <c>Error</c> (null on success).</summary>
		public static ImpunityYield TriggerEventYield(this IDistributedEntity entity, int eventType, BsonValue eventData,
			EntityEventEcho echo = EntityEventEcho.IncludeSender)
		{
			var t = new ImpunityYield();
			entity.TriggerEvent(eventType, eventData, echo, t.OnComplete);
			return t;
		}

		/// <summary>Coroutine wrapper for <see cref="IDistributedEntity.UpdateIfUnchanged"/>. Yield until the guarded update
		/// completes, then check the yield's <see cref="ImpunityYield{T}.Value"/>: <see cref="GuardedResult.Applied"/>,
		/// <see cref="GuardedResult.Stale"/> (lost the race), or <see cref="GuardedResult.Failed"/>, the only outcome
		/// that sets <see cref="ImpunityYield{T}.Error"/>.</summary>
		public static ImpunityYield<GuardedResult> UpdateIfUnchangedYield(this IDistributedEntity entity, GameStateActionBase? onUpdatedAction = null)
		{
			var t = new ImpunityYield<GuardedResult>();
			entity.UpdateIfUnchanged(t.OnComplete, onUpdatedAction);
			return t;
		}

		/// <summary>Coroutine wrapper for <c>IDistributedEntity.Delete</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<bool> DeleteYield(this IDistributedEntity entity, BsonValue deleteData, GameStateActionBase? onDeletedAction = null)
		{
			var t = new ImpunityYield<bool>();
			entity.Delete(deleteData, t.OnComplete, onDeletedAction);
			return t;
		}

		/// <summary>Coroutine wrapper for <see cref="IDistributedEntity.DeleteIfUnchanged"/>. Yield until the guarded delete
		/// completes, then check the yield's <see cref="ImpunityYield{T}.Value"/>: <see cref="GuardedResult.Applied"/>
		/// (deleted), <see cref="GuardedResult.Stale"/> (a guarded field changed first), or
		/// <see cref="GuardedResult.Failed"/>, the only outcome that sets <see cref="ImpunityYield{T}.Error"/>.</summary>
		public static ImpunityYield<GuardedResult> DeleteIfUnchangedYield(this IDistributedEntity entity, FieldGuard guard, BsonValue? deleteData, GameStateActionBase? onDeletedAction = null)
		{
			var t = new ImpunityYield<GuardedResult>();
			entity.DeleteIfUnchanged(guard, deleteData, t.OnComplete, onDeletedAction);
			return t;
		}

		/// <summary>Coroutine wrapper for <see cref="IDistributedEntity.ReplaceIfUnchanged{T}"/>. Yield until the guarded
		/// replace completes, then check the yield's <see cref="ImpunityYield{T}.Value"/>: <see cref="GuardedResult.Applied"/>
		/// (replaced), <see cref="GuardedResult.Stale"/> (a guarded field changed first), or
		/// <see cref="GuardedResult.Failed"/>, the only outcome that sets <see cref="ImpunityYield{T}.Error"/>.</summary>
		public static ImpunityYield<GuardedResult> ReplaceIfUnchangedYield<T>(this IDistributedEntity entity, FieldGuard guard, T newObject, BsonValue? replaceData = null, GameStateActionBase? onCreatedAction = null) where T : class, IDistributedObject
		{
			var t = new ImpunityYield<GuardedResult>();
			entity.ReplaceIfUnchanged(guard, newObject, replaceData, t.OnComplete, onCreatedAction);
			return t;
		}

		/// <summary>Coroutine wrapper for <c>IDistributedEntity.TryLock</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<bool> TryLockYield(this IDistributedEntity entity)
		{
			var t = new ImpunityYield<bool>();
			entity.TryLock(t.OnComplete);
			return t;
		}

		/// <summary>Coroutine wrapper for <c>IDistributedEntity.WaitForLock</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<LockWaitResult> WaitForLockYield(this IDistributedEntity entity)
		{
			var t = new ImpunityYield<LockWaitResult>();
			entity.WaitForLock(t.OnComplete);
			return t;
		}

		/// <summary>Coroutine wrapper for <c>IDistributedEntity.Unlock</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static ImpunityYield<bool> UnlockYield(this IDistributedEntity entity)
		{
			var t = new ImpunityYield<bool>();
			entity.Unlock(t.OnComplete);
			return t;
		}

		/// <summary>Coroutine wrapper for <see cref="IDistributedEntity.RunExclusive"/> with a synchronous body: yield
		/// until the lock has been acquired, the body has run and the lock has been released. Check the yield's
		/// <see cref="ImpunityYield{T}.Value"/> for the outcome.</summary>
		/// <param name="entity">The entity to hold the lock on.</param>
		/// <param name="body">The logic to run under the lock.</param>
		/// <param name="timeoutSeconds">How long to wait for the lock; negative uses the manager's default, zero does
		/// not wait.</param>
		public static ImpunityYield<RunExclusiveResult> RunExclusiveYield(this IDistributedEntity entity, Action body, float timeoutSeconds = -1f)
		{
			var t = new ImpunityYield<RunExclusiveResult>();
			entity.RunExclusive(body, t.OnComplete, timeoutSeconds);
			return t;
		}

		/// <summary>Coroutine wrapper for <see cref="IDistributedEntity.Transact"/>: yield until the transaction has
		/// committed, declined, run out of attempts or failed. Check the yield's <see cref="ImpunityYield{T}.Value"/> for
		/// the outcome; <see cref="ImpunityYield{T}.Error"/> is set only for <see cref="TransactResult.Failed"/>.</summary>
		/// <param name="entity">The entity to change.</param>
		/// <param name="body">Reads and sets the entity's fields; returns true to commit, false to send nothing. May run
		/// more than once, so it must have no side effects outside the entity.</param>
		/// <param name="maxAttempts">Runs of the body before <see cref="TransactResult.Conflict"/>; negative uses the
		/// manager's default.</param>
		public static ImpunityYield<TransactResult> TransactYield(this IDistributedEntity entity, Func<TransactScope, bool> body, int maxAttempts = -1)
		{
			var t = new ImpunityYield<TransactResult>();
			entity.Transact(body, t.OnComplete, maxAttempts);
			return t;
		}

		/// <summary>Coroutine wrapper for <see cref="IDistributedEntity.RunExclusive"/> with a coroutine body, for work
		/// that has to span frames. The lock is held for the whole of <paramref name="body"/>, so keep it short —
		/// other clients sit in the server's waiter queue meanwhile.</summary>
		/// <param name="entity">The entity to hold the lock on.</param>
		/// <param name="host">The behaviour that runs the body coroutine. It must stay alive and enabled for the
		/// duration of the scope; if it is destroyed mid-body the lock is only released when the connection drops.</param>
		/// <param name="body">The coroutine to run under the lock. Started once the lock is held and run to completion
		/// before the lock is released.</param>
		/// <param name="timeoutSeconds">How long to wait for the lock; negative uses the manager's default, zero does
		/// not wait. The body's own running time is never counted.</param>
		public static ImpunityYield<RunExclusiveResult> RunExclusiveYield(this IDistributedEntity entity, MonoBehaviour host, Func<IEnumerator> body, float timeoutSeconds = -1f)
		{
			var t = new ImpunityYield<RunExclusiveResult>();

			entity.Manager.RunExclusiveDeferred(entity, finish =>
			{
				try
				{
					host.StartCoroutine(RunBodyThenFinish(body(), finish));
				}
				catch (Exception e)
				{
					// The body factory threw, or the host is inactive/destroyed so the coroutine cannot start.
					finish(e);
				}
			}, t.OnComplete, timeoutSeconds);

			return t;
		}

		private static IEnumerator RunBodyThenFinish(IEnumerator body, Action<Exception?> finish)
		{
			// Unity coroutines cannot yield from inside a try/catch with a catch clause, so the body is driven by
			// hand: each MoveNext is guarded, and whatever it yields is passed straight through to the scheduler.
			while (true)
			{
				object current;
				try
				{
					if (!body.MoveNext())
					{
						break;
					}
					current = body.Current;
				}
				catch (Exception e)
				{
					finish(e);
					yield break;
				}

				yield return current;
			}

			finish(null);
		}

	}

	/// <summary>Unity coroutine yield extension methods for <see cref="IDistributedChannel"/>.</summary>
	public static class IDistributedChannelYieldExtensions
	{
		/// <summary>Coroutine wrapper for <c>IDistributedChannel.Unsubscribe</c>. Yield it, then check <c>Error</c> (null on success).</summary>
		public static ImpunityYield UnsubscribeYield(this IDistributedChannel channel, bool immediate = false)
		{
			var t = new ImpunityYield();
			channel.Unsubscribe(t.OnComplete, immediate);
			return t;
		}

	}
}
