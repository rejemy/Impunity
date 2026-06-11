using System;
using System.Collections.Generic;
using System.IO;
using UltraLiteDB;


namespace Impunity.GameState
{

	/// <summary>Runtime metadata for a registered distributed entity type, including its property definitions and lookup tables.</summary>
	public class GameStateEntityType
	{
		public int Index;

		public string Name = null!;

		public string? PersistedAs = null!;

		/// <summary>True if any property is temporal. Gates the cooldown-lock pre-scan on incoming updates.</summary>
		public bool HasTemporalProps;

		public GameStateEntityPropertyDef[] PackedProperties = null!;
		public GameStateEntityPropertyDef[] PropertyIndexLookup = null!;
		public Dictionary<string, GameStateEntityPropertyDef> PropertyPersistedAsLookup = null!;
	}

	/// <summary>Represents one client's view of the game state. Tracks which channels they're subscribed to, which entities they hold locks on, and their ephemeral (non-persisted) entities. One per connection.</summary>
	public class GameStateReplicant
	{
		/// <summary>The connection this replicant belongs to.</summary>
		public IServerSideConnectionProxy ConnectionProxy;
		/// <summary>Entities currently locked by this connection, keyed by entity ID.</summary>
		public Dictionary<uint, GameStateEntity> LocksHeld;
		/// <summary>Ephemeral entities owned by this connection (destroyed on disconnect).</summary>
		public Dictionary<uint, GameStateEntity> EphemeralEntities;
		/// <summary>Channels this connection is subscribed to, keyed by channel entity ID.</summary>
		public Dictionary<uint, GameStateChannel> Subscriptions;

		/// <summary>The connection's unique ID.</summary>
		public string Id { get { return ConnectionProxy.ConnectionId;  } }
		/// <summary>The connection's key for reconnection.</summary>
		public string ConnectionKey { get { return ConnectionProxy.ConnectionKey;  } }

		public GameStateReplicant(IServerSideConnectionProxy proxy)
		{
			ConnectionProxy = proxy;
			LocksHeld = new Dictionary<uint, GameStateEntity>();
			EphemeralEntities = new Dictionary<uint, GameStateEntity>();
			Subscriptions = new Dictionary<uint, GameStateChannel>();
		}

		/// <summary>Releases all locks and cleans up ephemeral entities owned by this replicant. Called on disconnect.</summary>
		public void Cleanup()
		{
			Dictionary<uint, GameStateEntity> locked = LocksHeld;
			LocksHeld = null!;
			foreach (GameStateEntity entity in locked.Values)
			{
				entity.TryUnlock(this);
			}
			locked.Clear();

			Dictionary<uint, GameStateEntity> ephemerals = EphemeralEntities;
			EphemeralEntities = null!;
			foreach (GameStateEntity entity in ephemerals.Values)
			{
				entity.Destroy(null);
			}
			ephemerals.Clear();

			foreach(GameStateChannel channel in Subscriptions.Values)
			{
				channel.RemoveListener(this);
			}
			Subscriptions.Clear();
		}

		public void SendMessageViaConnection(ServerActionBase message)
		{
			ConnectionProxy.SendMessageToClient(message);
		}

		public void AddLockedEntity(GameStateEntity entity)
		{
			entity.LockHeldBy = this;
			LocksHeld.Add(entity.Id, entity);
		}

		public void RemoveLockedEntity(GameStateEntity entity)
		{
			entity.LockHeldBy = null;
			if (LocksHeld != null)
			{
				LocksHeld.Remove(entity.Id);
			}
		}

		public void AddEphemeralEntity(GameStateEntity entity)
		{
			entity.EphemeralOwner = this;
			EphemeralEntities.Add(entity.Id, entity);
		}

		public void RemoveEphemeralEntity(GameStateEntity entity)
		{
			entity.EphemeralOwner = null;
			if (EphemeralEntities != null)
			{
				EphemeralEntities.Remove(entity.Id);
			}
		}

		public void AddSubscribedChannel(GameStateChannel channel)
		{
			Subscriptions[channel.Id] = channel;
		}

		public void RemoveSubscribedChannel(GameStateChannel channel)
		{
			Subscriptions.Remove(channel.Id);
		}

	}

	// Base for all live entities
	public abstract class GameStateEntity
	{
		public uint Id { get; internal set; }
		public string? Name { get; private set; }
		protected ImpunityInstanceFlags Flags;
		public byte FlagByte { get { return (byte)Flags; } }
		public abstract string? ChannelName { get; }
		public bool InLoadingState { get; protected set; } = false;

		protected GameStateLive LiveData;
		public GameStateEntityType? TypeInfo;

		public GameStateReplicant? LockHeldBy;
		public string? LockedWith { get; internal set; }

		public GameStateReplicant? EphemeralOwner;

		private IStandardDistributableValueType[] Properties = null!;

		/// <summary>Per-field last-received sequence numbers from clients, indexed by propId. Used to discard stale out-of-order updates.</summary>
		private ushort[] RecvSeq = null!;
		/// <summary>Per-field server time (ms) when the temporal cooldown lock expires, indexed by propId. 0 = no lock.</summary>
		private long[] CooldownExpiry = null!;
		/// <summary>Outgoing sequence counter for broadcasting updates to clients.</summary>
		public ushort OutSeq;

		public GameStateEntity(GameStateLive liveData, GameStateEntityType? typeInfo, byte instanceFlags, string? name = null)
		{
			LiveData = liveData;
			TypeInfo = typeInfo;
			Name = name;
			Flags = (ImpunityInstanceFlags)instanceFlags;

			if (typeInfo != null)
			{
				if (typeInfo.PackedProperties != null && typeInfo.PackedProperties.Length > 0)
				{
					int maxPropIndex = typeInfo.PackedProperties[typeInfo.PackedProperties.Length - 1].Index;
					Properties = new IStandardDistributableValueType[maxPropIndex + 1];
					RecvSeq = new ushort[maxPropIndex + 1];
					CooldownExpiry = new long[maxPropIndex + 1];

					foreach (GameStateEntityPropertyDef propDef in typeInfo.PackedProperties)
					{
						GameStateEntityFieldType fieldType = (GameStateEntityFieldType)propDef.FieldType;
						switch(fieldType)
						{
							case GameStateEntityFieldType.Value:
								Properties[propDef.Index] = DistributedValueFactory.MakeValue(propDef.PropValueType);
								break;
							case GameStateEntityFieldType.Array:
								Properties[propDef.Index] = DistributedValueFactory.MakeArray(propDef.PropValueType);
								break;
							case GameStateEntityFieldType.Queue:
								Properties[propDef.Index] = DistributedValueFactory.MakeQueue(propDef.PropValueType);
								break;
							case GameStateEntityFieldType.IntDictionary:
								Properties[propDef.Index] = DistributedValueFactory.MakeIntDictionary(propDef.PropValueType);
								break;
							case GameStateEntityFieldType.StringDictionary:
								Properties[propDef.Index] = DistributedValueFactory.MakeStringDictionary(propDef.PropValueType);
								break;
						}
						
					}
				}
			}
		}

		public bool IsClientAuthoritative()
		{
			return (Flags & ImpunityInstanceFlags.ClientAuthoritative) != 0;
		}

		public bool IsPersisted()
		{
			return (Flags & ImpunityInstanceFlags.Persisted) != 0;
		}

		public virtual bool Lock(GameStateReplicant lockedBy, bool waitForUnlock)
		{
			if (LockedWith != null && LockedWith != lockedBy.ConnectionKey)
			{
				return false;
			}

			LockedWith = lockedBy.ConnectionKey;
			lockedBy.AddLockedEntity(this);
			return true;
		}

		public bool TryUnlock(GameStateReplicant unlockedBy)
		{
			if (LockedWith != unlockedBy.ConnectionKey || LockHeldBy != unlockedBy)
			{
				return false;
			}

			Unlock(unlockedBy);

			return true;
		}

		protected virtual void Unlock(GameStateReplicant? unlockedBy)
		{
			LockedWith = null;
			if (LockHeldBy != null)
			{
				LockHeldBy.RemoveLockedEntity(this);
			}
		}

		public bool IsLocked()
		{
			return LockedWith != null;
		}

		public bool IsLockedBy(string key)
		{
			return LockedWith == key;
		}

		public bool IsAccessibleBy(string key)
		{
			return LockedWith == null || LockedWith == key;
		}

		/// <summary>
		/// Applies a client property update to this entity. Returns false if the entire update was dropped
		/// because it contains a temporal property that is still under a cooldown lock — in that case nothing
		/// is applied, persisted, or relayed. A temporal value's cooldown thereby acts as a lock guarding all
		/// other property updates batched in the same message.
		/// </summary>
		public virtual bool UpdateProps(BinaryReader propReader, ArraySegment<byte> propData, GameStateReplicant updatedBy,
						bool guaranteed, out List<LiveEntityPersistedPropertyData>? persistedProps, ushort seq = 0)
		{
			if (propData.Array == null || propData.Count == 0 || TypeInfo == null)
			{
				persistedProps = null;
				return true;
			}

			List<LiveEntityPersistedPropertyData>? persistedPropsSoFar = null;
			var propLookup = TypeInfo.PropertyIndexLookup;

			long currTime = GameStateServer.GetServerTime();

			if (TypeInfo.HasTemporalProps && HasActiveCooldownLock(propReader, propLookup, currTime))
			{
				persistedProps = null;
				return false;
			}

			while (true)
			{
				int propId = propReader.ReadByte();
				if (propId == 0)
				{
					break;
				}

				if (propId >= Properties.Length || Properties[propId] == null)
				{
					throw new ImpunityServerException(ImpunityErrorCode.ActionInvalidParameter, "Invalid property id: " + propId);
				}

				var propInfo = propLookup[propId];

				// Temporal field updates carry a requested cooldown lock duration before the value
				ushort cooldownMs = 0;
				if (propInfo.IsTemporal)
				{
					cooldownMs = propReader.ReadUInt16();
				}

				// Check per-field sequence number to discard stale out-of-order updates
				if (seq != 0 && RecvSeq != null && !((short)(seq - RecvSeq[propId]) > 0))
				{
					// Stale update for this field — skip its data without applying
					Properties[propId].SkipFrom(propReader);
					continue;
				}

				Properties[propId].ReadFrom(propReader);
				Properties[propId].LastModifiedTime = currTime;

				if (propInfo.IsTemporal && CooldownExpiry != null)
				{
					CooldownExpiry[propId] = cooldownMs > 0 ? currTime + cooldownMs : 0;
				}

				if (RecvSeq != null && seq != 0)
				{
					RecvSeq[propId] = seq;
				}

				if (propInfo.PersistedAs != null)
				{
					if (persistedPropsSoFar == null)
					{
						persistedPropsSoFar = new List<LiveEntityPersistedPropertyData>();
					}

					persistedPropsSoFar.Add(new LiveEntityPersistedPropertyData(propInfo.PersistedAs, Properties[propId].AsBsonValue()!));
				}
			}

			persistedProps = persistedPropsSoFar;
			return true;
		}

		/// <summary>
		/// Pre-scans a property update for temporal properties still under an active cooldown lock, without
		/// applying anything. Returns true if the update must be dropped. Restores the reader position.
		/// </summary>
		private bool HasActiveCooldownLock(BinaryReader propReader, GameStateEntityPropertyDef[] propLookup, long currTime)
		{
			long startPos = propReader.BaseStream.Position;
			bool locked = false;

			while (true)
			{
				int propId = propReader.ReadByte();
				if (propId == 0)
				{
					break;
				}

				if (propId >= Properties.Length || Properties[propId] == null)
				{
					throw new ImpunityServerException(ImpunityErrorCode.ActionInvalidParameter, "Invalid property id: " + propId);
				}

				if (propLookup[propId].IsTemporal)
				{
					propReader.ReadUInt16(); // requested cooldown, ignored during scan
					if (CooldownExpiry != null && currTime < CooldownExpiry[propId])
					{
						locked = true;
						break;
					}
				}

				Properties[propId].SkipFrom(propReader);
			}

			propReader.BaseStream.Position = startPos;
			return locked;
		}

		public void SetPersistedProps(List<LiveEntityPersistedPropertyData> props)
		{
			if (props == null || TypeInfo == null)
			{
				return;
			}

			foreach(LiveEntityPersistedPropertyData propData in props)
			{
				var propInfo = TypeInfo.PropertyPersistedAsLookup.GetValueOrDefault(propData.PropertyName);
				if (propInfo == null)
				{
					// Type no longer has a persisted property, should have been updated during format update process
					ImpunityLogger.LogError("Distributed type " + TypeInfo.Name + " has unknown persisted property name " + propData.PropertyName);
					continue;
				}
				var propertyInstance = Properties[propInfo.Index];
				propertyInstance.FromBsonValue(propData.PropertyValue);
			}
		}

		public ArraySegment<byte> GetPropBytes()
		{
			if (TypeInfo == null || TypeInfo.PackedProperties == null || TypeInfo.PackedProperties.Length == 0)
			{
				return default;
			}

			BinaryWriter writer = LiveData.GetTempBufferWriter();
			writer.BaseStream.Position = 0;

			long currServerTime = GameStateServer.GetServerTime();
			foreach (var propInfo in TypeInfo.PackedProperties)
			{
				writer.Write((byte)propInfo.Index);
				if (propInfo.IsTemporal)
				{
					writer.Write((uint)(currServerTime - Properties[propInfo.Index].LastModifiedTime));

					// Remaining cooldown lock so a joining client computes the correct lock expiry regardless of age
					long remainingCooldown = CooldownExpiry != null ? CooldownExpiry[propInfo.Index] - currServerTime : 0;
					if (remainingCooldown < 0)
					{
						remainingCooldown = 0;
					}
					else if (remainingCooldown > ushort.MaxValue)
					{
						remainingCooldown = ushort.MaxValue;
					}
					writer.Write((ushort)remainingCooldown);
				}
				Properties[propInfo.Index].WriteTo(writer);
			}

			writer.Write((byte)0);

			int length = (int)writer.BaseStream.Position;
			writer.BaseStream.Position = 0;

			byte[] propBytes = new byte[length];
			int bytesRead = writer.BaseStream.Read(propBytes, 0, length);
			writer.BaseStream.Position = 0;

			if (bytesRead != length)
			{
				throw new Exception("Incomplete read of prop bytes from write's base stream");
			}

			return propBytes;
		}

		public virtual void SendEvent(int eventType, BsonValue eventData)
		{
			throw new Exception("SendEvent unimplemented in abstract base class");
		}

		public virtual void Destroy(BsonValue? deleteData)
		{
			Cleanup();
		}

		public void Cleanup()
		{
			Unlock(null);
			if (EphemeralOwner != null)
			{
				EphemeralOwner.RemoveEphemeralEntity(this);
			}
		}

	}

	public class GameStateChannel : GameStateEntity
	{
		Dictionary<uint, GameStateObject> Members;
		Dictionary<string, GameStateReplicant> Listeners;
		public override string ChannelName { get { return Name!; } }
		Dictionary<string, GameStateObject> UniqueNames;

		public GameStateChannel(GameStateLive liveData, GameStateEntityType? typeInfo, byte instanceFlags, string name)
			: base(liveData, typeInfo, instanceFlags, name)
		{
			Members = new Dictionary<uint, GameStateObject>();
			Listeners = new Dictionary<string, GameStateReplicant>();
			UniqueNames = new Dictionary<string, GameStateObject>();
		}

		public void AddListener(GameStateReplicant replicant, bool sendCreate)
		{
			if (Listeners.ContainsKey(replicant.Id))
			{
				// Already listening
				return;
			}

			Listeners.Add(replicant.Id, replicant);

			if(!sendCreate || InLoadingState)
			{
				return;
			}

			ChannelCreateMessageAction channelCreate = new ChannelCreateMessageAction();
			channelCreate.ChannelId = Id;
			channelCreate.ChannelName = ChannelName;
			channelCreate.ChannelType = TypeInfo != null ? TypeInfo.Index : 0;
			channelCreate.IsLocked = IsLocked();
			channelCreate.InstanceFlags = (byte)Flags;
			channelCreate.PropBytes = GetPropBytes();
			channelCreate.ObjectsInChannel = new ObjectCreateMessageAction[Members.Count];

			int i = 0;
			foreach (GameStateObject obj in Members.Values)
			{
				channelCreate.ObjectsInChannel[i++] = obj.MakeCreateMessage();
			}

			replicant.SendMessageViaConnection(channelCreate);
		}

		public void RemoveListener(GameStateReplicant replicant)
		{
			Listeners.Remove(replicant.Id);
		}

		public bool HasUniqueName(string name)
		{
			return UniqueNames.ContainsKey(name);
		}

		public GameStateObject? GetNamedObject(string name)
		{
			return UniqueNames.GetValueOrDefault(name);
		}

		public void AddObject(GameStateObject obj, GameStateReplicant? addedBy)
		{
			Members.Add(obj.Id, obj);
			if (obj.Name != null)
			{
				UniqueNames.Add(obj.Name, obj);
			}
			obj.AddedToChannel(this);

			ObjectCreateMessageAction createMessage = obj.MakeCreateMessage();

			SendToListeners(createMessage, addedBy);
		}

		public void RemoveObject(GameStateObject obj)
		{
			Members.Remove(obj.Id);
			if (obj.Name != null)
			{
				UniqueNames.Remove(obj.Name);
			}
		}

		public override bool UpdateProps(BinaryReader propReader, ArraySegment<byte> propData, GameStateReplicant updatedBy,
						bool guaranteed, out List<LiveEntityPersistedPropertyData>? persistedProps, ushort seq = 0)
		{
			if (!base.UpdateProps(propReader, propData, updatedBy, guaranteed, out persistedProps, seq))
			{
				// Dropped by a temporal cooldown lock — relay nothing
				return false;
			}

			OutSeq++;
			EntityUpdateMessageAction updateMessage = new EntityUpdateMessageAction();
			updateMessage.SetGuaranteed(guaranteed);
			updateMessage.EntityId = Id;
			updateMessage.UpdateBytes = propData;
			updateMessage.Seq = OutSeq;

			GameStateReplicant? except = IsClientAuthoritative() ? updatedBy : null;

			SendToListeners(updateMessage, except);

			return true;
		}

		public override void SendEvent(int eventType, BsonValue eventData)
		{
			EntityEventMessageAction eventMessage = new EntityEventMessageAction();
			eventMessage.EntityId = Id;
			eventMessage.EventType = eventType;
			eventMessage.EventData = eventData;

			SendToListeners(eventMessage, null);
		}

		public override bool Lock(GameStateReplicant lockedBy, bool waitForUnlock)
		{
			if (base.Lock(lockedBy, waitForUnlock))
			{
				EntityLockedMessageAction lockedMessage = new EntityLockedMessageAction();
				lockedMessage.EntityId = Id;

				SendToListeners(lockedMessage, lockedBy);

				return true;
			}
			return false;
		}

		protected override void Unlock(GameStateReplicant? unlockedBy)
		{
			base.Unlock(unlockedBy);

			EntityUnlockedMessageAction unlockedMessage = new EntityUnlockedMessageAction();
			unlockedMessage.EntityId = Id;

			SendToListeners(unlockedMessage, unlockedBy);
		}

		public override void Destroy(BsonValue? deleteData)
		{
			base.Destroy(deleteData);

			EntityDeleteMessageAction deleteMessage = new EntityDeleteMessageAction();
			deleteMessage.EntityId = Id;
			deleteMessage.DeleteData = deleteData;

			SendToListeners(deleteMessage, null);

			foreach(GameStateObject member in Members.Values)
			{
				member.Cleanup();
				LiveData.UnregisterEntity(member);
			}

			Members.Clear();
			Listeners.Clear();
		}

		public void SendToListeners(ServerActionBase message, GameStateReplicant? except)
		{
			foreach (GameStateReplicant replicant in Listeners.Values)
			{
				if(except != null && except.Id == replicant.Id)
				{
					continue;
				}

				replicant.SendMessageViaConnection(message);
			}
		}
	}

	public interface GameStateChannelLoadListener
	{
		void OnChannelLoaded(GameStateServer game, ImpunityErrorResponse? error, GameStateChannel loadedChannel);
	}

	public class GameStateChannelLoadProxy : GameStateEntity
	{
		public override string ChannelName { get { return Name!; } }

		// Create if missing values
		bool CreateIfMissing;
		GameStateReplicant Creator = null!;
		int CreateEntityTypeId;
		byte CreateInstanceFlags;
		ArraySegment<byte> CreatePropBytes;
		List<ObjectCreateData>? CreateObjects;

		List<GameStateChannelLoadListener> LoadListeners;

		public GameStateChannelLoadProxy(GameStateLive liveData, string name)
										: base(liveData, null, 0, name)
		{
			InLoadingState = true;
			LoadListeners = new List<GameStateChannelLoadListener>();
		}

		public void AddListener(GameStateChannelLoadListener listener)
		{
			LoadListeners.Add(listener);
		}

		public void SetCreateIfMissing(GameStateReplicant creator, int entityTypeId, byte instanceFlags, ArraySegment<byte> propBytes, List<ObjectCreateData>? objects)
		{
			if (CreateIfMissing)
			{
				// First to set it wins
				return;
			}

			CreateIfMissing = true;
			Creator = creator;
			CreateEntityTypeId = entityTypeId;
			CreateInstanceFlags = instanceFlags;
			CreatePropBytes = propBytes;
			CreateObjects = objects;
		}

		public void OnDataLoaded(ImpunityErrorResponse? error, LiveChannelData channelData)
		{
			if (error == null && channelData == null && !CreateIfMissing)
			{
				error = new ImpunityErrorResponse(ImpunityErrorCode.ActionNotFound, "No channel with name " + Name);
			}

			if (error != null)
			{
				// DB error, or no channel in DB
				foreach(GameStateChannelLoadListener listener in LoadListeners)
				{
					try
					{
						listener.OnChannelLoaded(LiveData.Server, error, null!);
					}
					catch(Exception e)
					{
						ImpunityLogger.LogError("Exception in OnChannelLoad", e);
					}
				}

				// No longer need the load proxy
				LiveData.UnregisterEntity(this);
				LoadListeners.Clear();

				return;
			}

			GameStateChannel? channel = null;
			ImpunityErrorResponse? createError = null;
			try
			{
				if (channelData != null)
				{
					// Replaces this proxy with the newly loaded actual channel and all its objects
					channel = LiveData.CreateChannel(this, channelData);
				}
				else
				{
					// No channel found, but we're all set to create it
					LiveData.UnregisterEntity(this);
					channel = LiveData.CreateChannelInternal(Creator, ChannelName, CreateEntityTypeId, CreateInstanceFlags, CreatePropBytes, CreateObjects);
				}
			}
			catch (Exception e)
			{
				// error creating channel
				createError = new ImpunityErrorResponse(e);
			}

			foreach (GameStateChannelLoadListener listener in LoadListeners)
			{
				try
				{
					listener.OnChannelLoaded(LiveData.Server, createError, channel!);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception in OnChannelLoad", e);
				}
			}
		}
	}

	public class GameStateObject : GameStateEntity
	{
		public GameStateChannel? Channel { get; private set; }
		public override string? ChannelName { get { return Channel?.ChannelName; } }

		public GameStateObject(GameStateLive liveData, GameStateEntityType typeInfo, byte instanceFlags, string? dbid)
			: base(liveData, typeInfo, instanceFlags, dbid)
		{
			Channel = null;
		}

		public void AddedToChannel(GameStateChannel channel)
		{
			Channel = channel;
		}

		public ObjectCreateMessageAction MakeCreateMessage()
		{
			ObjectCreateMessageAction message = new ObjectCreateMessageAction();
			message.ObjectId = Id;
			message.ChannelId = Channel!.Id;
			message.ObjectType = TypeInfo!.Index;
			message.IsLocked = IsLocked();
			message.InstanceFlags = (byte)Flags;
			message.PropBytes = GetPropBytes();
			message.UniqueName = Name;
			return message;
		}

		public override bool UpdateProps(BinaryReader propReader, ArraySegment<byte> propData, GameStateReplicant updatedBy,
					bool guaranteed, out List<LiveEntityPersistedPropertyData>? persistedProps, ushort seq = 0)
		{
			if (!base.UpdateProps(propReader, propData, updatedBy, guaranteed, out persistedProps, seq))
			{
				// Dropped by a temporal cooldown lock — relay nothing
				return false;
			}

			if(Channel == null)
			{
				return true;
			}

			OutSeq++;
			EntityUpdateMessageAction updateMessage = new EntityUpdateMessageAction();
			updateMessage.SetGuaranteed(guaranteed);
			updateMessage.EntityId = Id;
			updateMessage.UpdateBytes = propData;
			updateMessage.Seq = OutSeq;

			GameStateReplicant? except = IsClientAuthoritative() ? updatedBy : null;

			Channel.SendToListeners(updateMessage, except);

			return true;
		}

		public override void SendEvent(int eventType, BsonValue eventData)
		{
			if(Channel == null)
			{
				return;
			}

			EntityEventMessageAction eventMessage = new EntityEventMessageAction();
			eventMessage.EntityId = Id;
			eventMessage.EventType = eventType;
			eventMessage.EventData = eventData;

			Channel.SendToListeners(eventMessage, null);
		}

		public override bool Lock(GameStateReplicant lockedBy, bool waitForUnlock)
		{
			if (base.Lock(lockedBy, waitForUnlock))
			{
				if (Channel != null)
				{
					EntityLockedMessageAction lockedMessage = new EntityLockedMessageAction();
					lockedMessage.EntityId = Id;

					Channel.SendToListeners(lockedMessage, lockedBy);
				}

				return true;
			}
			return false;
		}

		protected override void Unlock(GameStateReplicant? unlockedBy)
		{
			base.Unlock(unlockedBy);

			if (Channel != null)
			{
				EntityUnlockedMessageAction unlockedMessage = new EntityUnlockedMessageAction();
				unlockedMessage.EntityId = Id;

				Channel.SendToListeners(unlockedMessage, unlockedBy);
			}
		}

		public override void Destroy(BsonValue? deleteData)
		{
			base.Destroy(deleteData);

			if (Channel != null)
			{
				EntityDeleteMessageAction deleteMessage = new EntityDeleteMessageAction();
				deleteMessage.EntityId = Id;
				deleteMessage.DeleteData = deleteData;
				Channel.RemoveObject(this);

				Channel.SendToListeners(deleteMessage, null);
			}

			Channel = null;
		}
	}

	// Entity that exists only to be a lock, will be deleted when lock is released
	public class GameStateNamedLock : GameStateEntity
	{
		public override string ChannelName { get { return Name!; } }

		public List<GameStateReplicant>? LockAwaitedBy;

		public GameStateNamedLock(GameStateLive liveData, string name) : base (liveData, null, 0, name) {}

		public override bool Lock(GameStateReplicant lockedBy, bool waitForUnlock)
		{
			if (!base.Lock(lockedBy, waitForUnlock))
			{
				if (waitForUnlock)
				{
					if (LockAwaitedBy == null)
					{
						LockAwaitedBy = new List<GameStateReplicant>();
					}
					LockAwaitedBy.Add(lockedBy);
				}

				return false;
			}
			
			return true;
		}

		protected override void Unlock(GameStateReplicant? unlockedBy)
		{
			base.Unlock(unlockedBy);
			
			if (LockAwaitedBy != null)
			{
				NamedLockUnlockedMessageAction unlockedAction = new NamedLockUnlockedMessageAction();
				unlockedAction.Name = Name!;

				foreach(var replica in LockAwaitedBy)
				{
					replica.SendMessageViaConnection(unlockedAction);
				}

				LockAwaitedBy = null;
			}
		}
	}

	// Live gamestate in memory
	public class GameStateLive
	{
		public GameStateServer Server { get; private set; }
		GameStateEntityType[] EntityTypes = null!;

		Dictionary<uint, GameStateEntity> AllEntities;
		Dictionary<string, GameStateEntity> NamedEntities;

		HashSet<GameStateReplicant> ConnectedReplicas;
		public int NumConnections { get { return ConnectedReplicas.Count; } }

		uint NextId;

		private BinaryReader TempBufferReader;
		private BinaryWriter TempBufferWriter;

		public GameStateLive(GameStateServer server)
		{
			Server = server;

			AllEntities = new Dictionary<uint, GameStateEntity>();
			NamedEntities = new Dictionary<string, GameStateEntity>();
			ConnectedReplicas = new HashSet<GameStateReplicant>();

			TempBufferReader = new BinaryReader(new MemoryStream(new byte[ImpunityConstants.MaxMessageSize]));
			TempBufferWriter = new BinaryWriter(new MemoryStream(new byte[ImpunityConstants.MaxMessageSize]));
		}

		public BinaryWriter GetTempBufferWriter()
		{
			return TempBufferWriter;
		}

		public void SetFormat(GameStateEntityTypeDef[]? entityTypes)
		{
			if (entityTypes == null || entityTypes.Length < 1)
			{
				return;
			}

			int highestIndex = entityTypes[entityTypes.Length - 1].Index;

			EntityTypes = new GameStateEntityType[highestIndex + 1];
			for (int i = 0; i < entityTypes.Length; i++)
			{
				GameStateEntityTypeDef typeInfo = entityTypes[i];
				EntityTypes[typeInfo.Index] = ConvertEntityTypeDef(typeInfo);
			}
		}

		private GameStateEntityType ConvertEntityTypeDef(GameStateEntityTypeDef def)
		{
			GameStateEntityType etype = new GameStateEntityType();
			etype.Index = def.Index;
			etype.Name = def.Name;
			etype.PersistedAs = def.PersistedAs;
			
			etype.PackedProperties = def.Properties;
			if (etype.PackedProperties != null && etype.PackedProperties.Length > 0)
			{
				int highestIndex = etype.PackedProperties[etype.PackedProperties.Length - 1].Index;
				etype.PropertyIndexLookup = new GameStateEntityPropertyDef[highestIndex+1];
				etype.PropertyPersistedAsLookup = new Dictionary<string, GameStateEntityPropertyDef>();

				foreach(var prop in etype.PackedProperties)
				{
					etype.PropertyIndexLookup[prop.Index] = prop;
					if(prop.PersistedAs != null)
					{
						etype.PropertyPersistedAsLookup.Add(prop.PersistedAs, prop);
					}
					if (prop.IsTemporal)
					{
						etype.HasTemporalProps = true;
					}
				}
			}

			return etype;
		}

		public void AddGameStateReplicant(GameStateReplicant replica)
		{
			ConnectedReplicas.Add(replica);
		}

		public void RemoveGameStateReplicant(GameStateReplicant replica)
		{
			ConnectedReplicas.Remove(replica);
			replica.Cleanup();
		}

		uint FindAvailableEntityId()
		{
			return NextId++;
		}

		public void RegisterEntity(GameStateEntity entity)
		{
			entity.Id = FindAvailableEntityId();
			AllEntities[entity.Id] = entity;
			if (entity.Name != null)
			{
				NamedEntities[entity.Name] = entity;
			}

		}

		public void SwapOutEntity(GameStateEntity oldEntity, GameStateEntity newEntity)
		{
			newEntity.Id = oldEntity.Id;
			AllEntities[newEntity.Id] = newEntity;
			if (newEntity.Name != null)
			{
				NamedEntities[newEntity.Name] = newEntity;
			}

		}

		private void DestroyEntity(GameStateEntity entity, BsonValue? deleteData)
		{
			UnregisterEntity(entity);
			entity.Destroy(deleteData);
		}

		public void UnregisterEntity(GameStateEntity entity)
		{
			AllEntities.Remove(entity.Id);
			if (entity.Name != null)
			{
				NamedEntities.Remove(entity.Name);
			}
		}

		public GameStateEntityType GetEntityType(int typeId)
		{
			if (typeId <= 0 || typeId >= EntityTypes.Length)
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionInvalidParameter, "TypeId out of range");
			}
			GameStateEntityType typeInfo = EntityTypes[typeId];
			if (typeInfo == null)
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionInvalidParameter, "Invalid TypeId");
			}

			return typeInfo;
		}

		private bool UpdateEntityProps(GameStateEntity entity, ArraySegment<byte> propBytes, GameStateReplicant updatedBy,
							bool guaranteed, out List<LiveEntityPersistedPropertyData>? persistedProps, ushort seq = 0)
		{
			if (propBytes.Array == null || propBytes.Count == 0)
			{
				persistedProps = null;
				return true;
			}

			// Put prop data into a read buffer
			TempBufferReader.BaseStream.Position = 0;
			TempBufferReader.BaseStream.Write(propBytes);
			TempBufferReader.BaseStream.Position = 0;

			return entity.UpdateProps(TempBufferReader, propBytes, updatedBy, guaranteed, out persistedProps, seq);
		}

		// ----- Public API below

		public GameStateEntity? GetNamedEntity(string entityName)
		{
			return NamedEntities.GetValueOrDefault(entityName);
		}

		public GameStateChannelLoadProxy LoadChannel(string channelName)
		{
			GameStateEntity? entity = NamedEntities.GetValueOrDefault(channelName);
			if (entity is GameStateChannelLoadProxy)
			{
				return (GameStateChannelLoadProxy)entity;
			}
			else if(entity != null)
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionBadRequest, "Entity with name " + channelName + " already loaded");
			}

			GameStateChannelLoadProxy loadProxy = new GameStateChannelLoadProxy(this, channelName);
			RegisterEntity(loadProxy);

			LoadChannelAction loadAction = new LoadChannelAction(channelName);

			loadAction.OnCompleteCallback = loadProxy.OnDataLoaded;
			Server.QueueAction(loadAction);

			return loadProxy;
		}

		// Create channel from DB info
		public GameStateChannel CreateChannel(GameStateChannelLoadProxy proxy, LiveChannelData channelData)
		{
			GameStateEntityType? typeInfo = null;
			if (channelData.EntityType != 0)
			{
				typeInfo = GetEntityType(channelData.EntityType);
			}

			GameStateChannel channel = new GameStateChannel(this, typeInfo, channelData.InstanceFlags, channelData.EntityId);
			channel.SetPersistedProps(channelData.Properties);

			if(channelData.ChannelObjects != null)
			{
				foreach(var objData in channelData.ChannelObjects)
				{
					CreateObject(objData, channel);
				}
			}

			SwapOutEntity(proxy, channel);

			return channel;
		}

		// Create channel from client input
		public bool CreateChannel(GameStateReplicant origin, string channelName, bool replace, int typeId, byte instanceFlags, ArraySegment<byte> propBytes, List<ObjectCreateData>? objects)
		{
			GameStateChannel? existing = NamedEntities.GetValueOrDefault(channelName) as GameStateChannel;
			
			if (replace && existing != null)
			{
				if (!DeleteEntity(origin, existing.Id, null))
				{
					throw new ImpunityServerException(ImpunityErrorCode.ActionBlockedByLock, "Couldn't replace channel " + channelName + " because existing channel was locked by someone else");
				}
			}

			GameStateChannel channel = CreateChannelInternal(origin, channelName, typeId, instanceFlags, propBytes, objects);

			return true;
		}

		public GameStateChannel CreateChannelInternal(GameStateReplicant origin, string channelName, int typeId, byte instanceFlags, ArraySegment<byte> propBytes, List<ObjectCreateData>? objects)
		{
			if (channelName == null)
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionInvalidParameter, "Name must be set for channel");
			}

			if (NamedEntities.ContainsKey(channelName))
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionUniqueNameExists, "Entity with name " + channelName + " already exists");
			}

			GameStateEntityType? typeInfo = null;
			if (typeId != 0)
			{
				typeInfo = GetEntityType(typeId);
			}

			GameStateChannel channel = new GameStateChannel(this, typeInfo, instanceFlags, channelName);
			List<LiveEntityPersistedPropertyData>? persistedProps;
			UpdateEntityProps(channel, propBytes, origin, true, out persistedProps);
			RegisterEntity(channel);

			if(channel.IsClientAuthoritative())
			{
				channel.Lock(origin, false);
			}

			if (channel.IsPersisted() && typeInfo?.PersistedAs != null)
			{
				// Save to DB
				CreatePersistedEntityAction action = new CreatePersistedEntityAction(channel.Name!, channel.ChannelName, typeId, channel.FlagByte, persistedProps);
				Server.QueueAction(action);
			}

			if (objects != null)
			{
				foreach(ObjectCreateData co in objects)
				{
					CreateObject(origin, co.EntityTypeId, co.InstanceFlags, channel.Id, co.PropBytes, co.UniqueName, true, true);
				}
			}

			return channel;
		}

		public uint SubscribeToChannel(GameStateReplicant origin, string channelName)
		{
			GameStateChannel? channel = NamedEntities.GetValueOrDefault(channelName) as GameStateChannel;
			if (channel == null)
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionNotFound, "No channel with name " + channelName);
			}

			channel.AddListener(origin, true);

			return channel.Id;
		}


		public void UnsubscribeFromChannel(GameStateReplicant origin, uint channelId)
		{
			GameStateChannel? channel = AllEntities.GetValueOrDefault(channelId) as GameStateChannel;
			if (channel == null)
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionNotFound, "No channel with id " + channelId);
			}

			channel.RemoveListener(origin);
		}

		public uint CreateObject(GameStateReplicant origin, int typeId, byte instanceFlags, uint channelId, ArraySegment<byte> propBytes, string? uniqueName, bool replace, bool force)
		{
			GameStateEntityType typeInfo = GetEntityType(typeId);

			GameStateChannel? channel = AllEntities.GetValueOrDefault(channelId) as GameStateChannel;
			if (channel == null || channel.InLoadingState)
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionNotFound, "No channel with ID " + channelId);
			}

			if (uniqueName != null && channel.HasUniqueName(uniqueName))
			{
				if (replace)
				{
					GameStateObject existing = channel.GetNamedObject(uniqueName)!;

					if (force)
					{
						DestroyEntity(existing, null);
					}
					else
					{
						if (!DeleteEntity(origin, existing.Id, null))
						{
							throw new ImpunityServerException(ImpunityErrorCode.ActionBlockedByLock, "Couldn't replace object " + uniqueName + " because existing object was locked by someone else");
						}
					}
				}
				throw new ImpunityServerException(ImpunityErrorCode.ActionUniqueNameExists, "Channel already has object with name " + uniqueName);
			}

			string? dbid = uniqueName;
			if (((ImpunityInstanceFlags)instanceFlags & ImpunityInstanceFlags.Persisted) != 0 && typeInfo.PersistedAs != null && dbid == null)
			{
				// If this is a persisted item and it doesn't have a unique name set, make one with a guid
				dbid = Guid.NewGuid().ToString();
			}

			GameStateObject dobj = new GameStateObject(this, typeInfo, instanceFlags, dbid);

			List<LiveEntityPersistedPropertyData>? persistedProps;
			UpdateEntityProps(dobj, propBytes, origin, true, out persistedProps);
			RegisterEntity(dobj);

			if (dobj.IsClientAuthoritative())
			{
				dobj.Lock(origin, false);
			}

			// Will notify all listeners (except origin) of new object
			channel.AddObject(dobj, origin);

			if (dobj.IsPersisted() && typeInfo.PersistedAs != null)
			{
				// Save to DB
				CreatePersistedEntityAction action = new CreatePersistedEntityAction(dobj.Name!, dobj.ChannelName!, typeId, channel.FlagByte, persistedProps);
				Server.QueueAction(action);
			}

			return dobj.Id;
		}

		public GameStateObject CreateObject(LiveEntityData objData, GameStateChannel channel)
		{
			GameStateEntityType typeInfo = GetEntityType(objData.EntityType);

			GameStateObject dobj = new GameStateObject(this, typeInfo, objData.InstanceFlags, objData.EntityId);
			dobj.SetPersistedProps(objData.Properties);

			RegisterEntity(dobj);

			channel.AddObject(dobj, null);

			return dobj;
		}

		public bool UpdateEntity(GameStateReplicant origin, uint entityId, ArraySegment<byte> propData, bool guaranteed, ushort seq = 0)
		{
			GameStateEntity? entity = AllEntities.GetValueOrDefault(entityId);
			if (entity == null || entity.InLoadingState)
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionNotFound, "No entity with ID " + entityId);
			}

			if (!entity.IsAccessibleBy(origin.ConnectionKey))
			{
				// Can't update locked entity
				return false;
			}

			List<LiveEntityPersistedPropertyData>? persistedProps;
			if (!UpdateEntityProps(entity, propData, origin, guaranteed, out persistedProps, seq))
			{
				// Entire update silently dropped by a temporal cooldown lock
				return false;
			}

			if (entity.IsPersisted() && persistedProps != null)
			{
				UpdatePersistedEntityPropertiesAction action = new UpdatePersistedEntityPropertiesAction(entity.Name!, entity.ChannelName!, persistedProps);
				Server.QueueAction(action);
			}

			return true;
		}

		

		public void SendEntityEvent(uint entityId, int eventType, BsonValue eventData)
		{
			GameStateEntity? entity = AllEntities.GetValueOrDefault(entityId);
			if (entity == null || entity.InLoadingState)
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionNotFound, "No entity with ID " + entityId);
			}

			entity.SendEvent(eventType, eventData);
		}

		public bool DeleteEntity(GameStateReplicant origin, uint entityId, BsonValue? deleteData)
		{
			GameStateEntity? entity = AllEntities.GetValueOrDefault(entityId);
			if (entity == null || entity.InLoadingState)
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionNotFound, "No entity with ID " + entityId);
			}

			if (!entity.IsAccessibleBy(origin.ConnectionKey))
			{
				// Can't delete locked entity
				return false;
			}

			DestroyEntity(entity, deleteData);

			return true;
		}

		public bool LockEntity(GameStateReplicant origin, uint entityId)
		{
			GameStateEntity? entity = AllEntities.GetValueOrDefault(entityId);
			if (entity == null || entity.InLoadingState)
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionNotFound, "No entity with ID " + entityId);
			}

			if (!entity.Lock(origin, false))
			{
				// Already locked
				return false;
			}

			return true;
		}

		public bool UnlockEntity(GameStateReplicant origin, uint entityId)
		{
			GameStateEntity? entity = AllEntities.GetValueOrDefault(entityId);
			if (entity == null || entity.InLoadingState)
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionNotFound, "No entity with ID " + entityId);
			}

			return entity.TryUnlock(origin);
		}

		public bool TryToLockNamedLock(GameStateReplicant origin, string name, bool waitForUnlock)
		{
			GameStateEntity? entity = NamedEntities.GetValueOrDefault(name);
			if (entity == null || entity.InLoadingState)
			{
				// Create placeholder named lock object if no entity with that name exists
				entity = new GameStateNamedLock(this, name);
				RegisterEntity(entity);
				origin.AddEphemeralEntity(entity);
			}

			bool locked = entity.Lock(origin, waitForUnlock);
			
			return locked;
		}

		public bool UnlockNamedLock(GameStateReplicant origin, string name)
		{
			GameStateEntity? entity = NamedEntities.GetValueOrDefault(name);
			if (entity == null || entity.InLoadingState)
			{
				return false;
			}

			bool unlocked = entity.TryUnlock(origin);
			if (unlocked)
			{
				if (entity is GameStateNamedLock)
				{
					DestroyEntity(entity, null);
				}
			}

			return unlocked;
		}

		public void SendBroadcastMessage(int messageType, BsonValue message, string fromConnectionId)
		{
			BroadcastMessageAction broadcastAction = new BroadcastMessageAction();

			broadcastAction.MessageType = messageType;
			broadcastAction.MessageBody = message;
			broadcastAction.SentBy = fromConnectionId;

			foreach (GameStateReplicant replica in ConnectedReplicas)
			{
				replica.SendMessageViaConnection(broadcastAction);
			}
		}


	}
}