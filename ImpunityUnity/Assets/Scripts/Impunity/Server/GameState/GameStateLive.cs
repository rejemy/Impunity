using System;
using System.Collections.Generic;
using System.IO;
using UltraLiteDB;


namespace Impunity.GameState
{


	// One window into the game state, maps to a single computer, either local or over network
	// Not necessarily a single player/user
	public class GameStateReplicant
	{
		public IServerSideConnectionProxy ConnectionProxy;
		public Dictionary<uint, GameStateEntity> LocksHeld;
		public Dictionary<uint, GameStateEntity> EphemeralEntities;
		public Dictionary<uint, GameStateChannel> Subscriptions;

		//public Dictionary<uint, GameStateObject> DistributedObjects;

		public string Id { get { return ConnectionProxy.ConnectionId;  } }

		public GameStateReplicant(IServerSideConnectionProxy proxy)
		{
			ConnectionProxy = proxy;
			LocksHeld = new Dictionary<uint, GameStateEntity>();
			EphemeralEntities = new Dictionary<uint, GameStateEntity>();
			Subscriptions = new Dictionary<uint, GameStateChannel>();

			//DistributedObjects = new Dictionary<uint, GameStateObject>();
		}

		public void Cleanup()
		{
			Dictionary<uint, GameStateEntity> locked = LocksHeld;
			LocksHeld = null;
			foreach (GameStateEntity entity in locked.Values)
			{
				entity.Unlock();
			}
			locked.Clear();

			Dictionary<uint, GameStateEntity> ephemerals = EphemeralEntities;
			EphemeralEntities = null;
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
		public string Name { get; private set; } // Not all entities have names

		protected GameStateLive LiveData;
		public GameStateEntityType TypeInfo;

		public GameStateReplicant LockHeldBy;
		public string LockedWith { get; internal set; }

		public GameStateReplicant EphemeralOwner;

		private IDistributableValueType[] Properties;

		public GameStateEntity(GameStateLive liveData, GameStateEntityType typeInfo, string name = null)
		{
			LiveData = liveData;
			TypeInfo = typeInfo;
			Name = name;

			if (typeInfo != null)
			{
				if (typeInfo.Properties != null && typeInfo.Properties.Length > 0)
				{
					int maxPropIndex = typeInfo.Properties[typeInfo.Properties.Length - 1].Index;
					Properties = new IDistributableValueType[maxPropIndex + 1];

					foreach (GameStateEntityPropertyDef propDef in typeInfo.Properties)
					{
						Properties[propDef.Index] = DistributedValueFactory.Make(propDef.PropValueType);
					}
				}
			}
		}

		public bool Lock(string key, GameStateReplicant lockedBy)
		{
			if (LockedWith != null && LockedWith != key)
			{
				return false;
			}

			LockedWith = key;
			lockedBy.AddLockedEntity(this);
			return true;
		}

		public bool Unlock(string key, GameStateReplicant unlockedBy)
		{
			if (LockedWith != key || LockHeldBy != unlockedBy)
			{
				return false;
			}

			LockedWith = null;
			LockHeldBy.RemoveLockedEntity(this);
			return true;
		}

		public void Unlock()
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

		public virtual void UpdateProps(BinaryReader propReader, byte[] propData)
		{
			if (propData == null || propData.Length == 0)
			{
				return;
			}

			while(true)
			{
				int propId = propReader.ReadByte();
				if (propId == 0)
				{
					break;
				}

				if(propId >= Properties.Length)
				{
					throw new Exception("Invalid property id: " + propId);
				}

				Properties[propId].ReadFrom(propReader);
			}
		}

		public byte[] GetPropBytes()
		{
			if (TypeInfo == null || TypeInfo.Properties == null || TypeInfo.Properties.Length == 0)
			{
				return null;
			}

			BinaryWriter writer = LiveData.GetTempBufferWriter();
			writer.BaseStream.Position = 0;

			foreach(var propInfo in TypeInfo.Properties)
			{
				writer.Write((byte)propInfo.Index);
				Properties[propInfo.Index].WriteTo(writer);
			}

			writer.Write((byte)0);

			int length = (int)writer.BaseStream.Position;
			writer.BaseStream.Position = 0;

			byte[] propBytes = new byte[length];
			writer.BaseStream.Read(propBytes, 0, length);
			writer.BaseStream.Position = 0;

			return propBytes;
		}

		public virtual void SendEvent(int eventType, BsonValue eventData)
		{

		}

		public virtual void Destroy(BsonValue deleteData)
		{
			Cleanup();
		}

		public void Cleanup()
		{
			Unlock();
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

		public GameStateChannel(GameStateLive liveData, GameStateEntityType typeInfo, string name) : base(liveData, typeInfo, name)
		{
			Members = new Dictionary<uint, GameStateObject>();
			Listeners = new Dictionary<string, GameStateReplicant>();
		}

		public void AddListener(GameStateReplicant replicant, bool sendCreate)
		{
			if (Listeners.ContainsKey(replicant.Id))
			{
				// Already listening
				return;
			}

			Listeners.Add(replicant.Id, replicant);

			if(!sendCreate)
			{
				return;
			}

			ChannelCreateMessageAction channelCreate = new ChannelCreateMessageAction();
			channelCreate.ChannelId = Id;
			channelCreate.ChannelName = Name;
			channelCreate.ChannelType = TypeInfo.Index;
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

		public void AddObject(GameStateObject obj, GameStateReplicant addedBy)
		{
			Members.Add(obj.Id, obj);
			obj.AddedToChannel(this);

			ObjectCreateMessageAction createMessage = obj.MakeCreateMessage();

			SendToListeners(createMessage, addedBy);
		}

		public void RemoveObject(GameStateObject obj)
		{
			Members.Remove(obj.Id);
		}

		public override void UpdateProps(BinaryReader propReader, byte[] propData)
		{
			base.UpdateProps(propReader, propData);

			EntityUpdateMessageAction updateMessage = new EntityUpdateMessageAction();
			updateMessage.EntityId = Id;
			updateMessage.UpdateBytes = propData;

			SendToListeners(updateMessage, null);
		}

		public override void SendEvent(int eventType, BsonValue eventData)
		{
			EntityEventMessageAction eventMessage = new EntityEventMessageAction();
			eventMessage.EntityId = Id;
			eventMessage.EventType = eventType;
			eventMessage.EventData = eventData;

			SendToListeners(eventMessage, null);
		}


		public override void Destroy(BsonValue deleteData)
		{
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

			base.Destroy(deleteData);
		}

		public void SendToListeners(ServerActionBase message, GameStateReplicant except)
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

	public class GameStateObject : GameStateEntity
	{
		GameStateChannel Channel;

		public GameStateObject(GameStateLive liveData, GameStateEntityType typeInfo) : base(liveData, typeInfo, null)
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
			message.ChannelId = Channel.Id;
			message.ObjectType = TypeInfo.Index;
			message.PropBytes = GetPropBytes();

			return message;
		}

		public override void UpdateProps(BinaryReader propReader, byte[] propData)
		{
			base.UpdateProps(propReader, propData);

			if(Channel == null)
			{
				return;
			}

			EntityUpdateMessageAction updateMessage = new EntityUpdateMessageAction();
			updateMessage.EntityId = Id;
			updateMessage.UpdateBytes = propData;

			Channel.SendToListeners(updateMessage, null);
		}

		public override void SendEvent(int eventType, BsonValue eventData)
		{
			EntityEventMessageAction eventMessage = new EntityEventMessageAction();
			eventMessage.EntityId = Id;
			eventMessage.EventType = eventType;
			eventMessage.EventData = eventData;

			Channel.SendToListeners(eventMessage, null);
		}

		public override void Destroy(BsonValue deleteData)
		{
			EntityDeleteMessageAction deleteMessage = new EntityDeleteMessageAction();
			deleteMessage.EntityId = Id;
			deleteMessage.DeleteData = deleteData;
			Channel.RemoveObject(this);

			Channel.SendToListeners(deleteMessage, null);
			Channel = null;

			base.Destroy(deleteData);
		}
	}

	// Entity that exists only to be a lock, will be deleted when lock is released
	public class GameStateNamedLock : GameStateEntity
	{
		public GameStateNamedLock(GameStateLive liveData, string name) : base (liveData, null, name)
		{
			
		}
	}

	// Live gamestate in memory
	public class GameStateLive
	{
		GameStateDB DB;

		GameStateEntityType[] EntityTypes;

		Dictionary<uint, GameStateEntity> AllEntities;
		Dictionary<string, GameStateEntity> NamedEntities;

		HashSet<GameStateReplicant> ConnectedReplicas;

		uint NextId;

		private BinaryReader TempBufferReader;
		private BinaryWriter TempBufferWriter;

		public GameStateLive(GameStateDB db)
		{
			DB = db;
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

		public void EnsureFormat(GameStateFormatData format)
		{
			if (format.EntityTypes == null || format.EntityTypes.Length < 1)
			{
				return;
			}

			int highestIndex = format.EntityTypes[format.EntityTypes.Length - 1].Index;

			EntityTypes = new GameStateEntityType[highestIndex + 1];
			for (int i = 0; i < format.EntityTypes.Length; i++)
			{
				GameStateEntityType typeInfo = format.EntityTypes[i];
				EntityTypes[typeInfo.Index] = typeInfo;
			}
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

		private void DestroyEntity(GameStateEntity entity, BsonValue deleteData)
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

		private GameStateEntityType GetEntityType(int typeId)
		{
			if (typeId <= 0 || typeId >= EntityTypes.Length)
			{
				throw new Exception("TypeId out of range");
			}
			GameStateEntityType typeInfo = EntityTypes[typeId];
			if (typeInfo == null)
			{
				throw new Exception("Invalid TypeId");
			}

			return typeInfo;
		}

		private void UpdateEntityProps(GameStateEntity entity, byte[] propBytes)
		{
			if (propBytes == null || propBytes.Length == 0)
			{
				return;
			}

			// Put prop data into a read buffer
			TempBufferReader.BaseStream.Position = 0;
			TempBufferReader.BaseStream.Write(propBytes);
			TempBufferReader.BaseStream.Position = 0;

			entity.UpdateProps(TempBufferReader, propBytes);
		}

		// ----- Public API below


		public uint CreateChannel(GameStateReplicant origin, int typeId, string name, byte[] propBytes)
		{
			if (name == null)
			{
				throw new Exception("Name must be set for channel");
			}

			if (NamedEntities.ContainsKey(name))
			{
				GameStateEntity existingEnt = NamedEntities[name];
				if (existingEnt.TypeInfo != null && existingEnt.TypeInfo.Index == typeId)
				{
					// Existing channel of the same type already created
					GameStateChannel existingChannel = (GameStateChannel)existingEnt;

					// subscribe to existing channel instead of erroring
					existingChannel.AddListener(origin, true);
					origin.AddSubscribedChannel(existingChannel);

					return existingChannel.Id;
				}
				else
				{
					throw new Exception("Entity with name " + name + " already exists");
				}
			}

			GameStateEntityType typeInfo = null;
			if (typeId != 0)
			{
				typeInfo = GetEntityType(typeId);
			}

			GameStateChannel channel = new GameStateChannel(this, typeInfo, name);
			UpdateEntityProps(channel, propBytes);
			RegisterEntity(channel);

			channel.AddListener(origin, false);
			origin.AddSubscribedChannel(channel);

			return channel.Id;
		}

		public uint CreateObject(GameStateReplicant origin, int typeId, uint channelId, byte[] propBytes)
		{
			GameStateEntityType typeInfo = GetEntityType(typeId);

			GameStateChannel channel = AllEntities.GetValueOrDefault(channelId) as GameStateChannel;
			if (channel == null)
			{
				throw new Exception("No channel with ID " + channelId);
			}

			GameStateObject dobj = new GameStateObject(this, typeInfo);
			UpdateEntityProps(dobj, propBytes);
			RegisterEntity(dobj);

			// Will notify all listeners (expect origin) of new object
			channel.AddObject(dobj, origin);

			return dobj.Id;
		}

		public bool UpdateEntity(uint entityId, string key, byte[] propData)
		{
			GameStateEntity entity = AllEntities[entityId];
			if (entity == null)
			{
				throw new Exception("No entity with ID " + entityId);
			}

			if (!entity.IsAccessibleBy(key))
			{
				// Can't update locked entity
				return false;
			}

			UpdateEntityProps(entity, propData);

			return true;
		}



		public void SendEntityEvent(uint entityId, int eventType, BsonValue eventData)
		{
			GameStateEntity entity = AllEntities.GetValueOrDefault(entityId);
			if (entity == null)
			{
				throw new Exception("No entity with ID " + entityId);
			}

			entity.SendEvent(eventType, eventData);
		}

		public bool DeleteEntity(uint entityId, string key, BsonValue deleteData)
		{
			GameStateEntity entity = AllEntities.GetValueOrDefault(entityId);
			if (entity == null)
			{
				throw new Exception("No entity with ID " + entityId);
			}

			if (!entity.IsAccessibleBy(key))
			{
				// Can't delete locked entity
				return false;
			}

			DestroyEntity(entity, deleteData);

			return true;
		}

		public bool LockEntity(GameStateReplicant origin, uint entityId, string key)
		{
			GameStateEntity entity = AllEntities.GetValueOrDefault(entityId);
			if (entity == null)
			{
				throw new Exception("No entity with ID " + entityId);
			}

			if (entity.IsLocked())
			{
				// Already locked
				return entity.IsLockedBy(key);
			}

			entity.Lock(key, origin);

			return true;
		}

		public bool UnlockEntity(GameStateReplicant origin, uint entityId, string key)
		{
			GameStateEntity entity = AllEntities.GetValueOrDefault(entityId);
			if (entity == null)
			{
				throw new Exception("No entity with ID " + entityId);
			}

			return entity.Unlock(key, origin);
		}

		public bool TryToLockNamedLock(GameStateReplicant origin, string name, string key)
		{
			GameStateEntity entity = NamedEntities.GetValueOrDefault(name);
			if (entity == null)
			{
				// Create placeholder named lock object if no entity with that name exists
				entity = new GameStateNamedLock(this, name);
				RegisterEntity(entity);
				origin.AddEphemeralEntity(entity);
			}

			bool locked = entity.Lock(key, origin);
			
			return locked;
		}

		public bool UnlockNamedLock(GameStateReplicant origin, string name, string key)
		{
			GameStateEntity entity = NamedEntities.GetValueOrDefault(name);
			if (entity == null)
			{
				return false;
			}

			bool unlocked = entity.Unlock(key, origin);
			if (unlocked)
			{
				if (entity is GameStateNamedLock)
				{
					DestroyEntity(entity, null);
				}
			}

			return unlocked;
		}

		public uint SubscribeToChannel(GameStateReplicant origin, string channelName)
		{
			GameStateChannel channel = NamedEntities.GetValueOrDefault(channelName) as GameStateChannel;
			if (channel == null)
			{
				throw new Exception("No channel with name " + channelName);
			}

			channel.AddListener(origin, true);

			return channel.Id;
		}

		public void UnsubscribeFromChannel(GameStateReplicant origin, uint channelId)
		{
			GameStateChannel channel = AllEntities.GetValueOrDefault(channelId) as GameStateChannel;
			if (channel == null)
			{
				throw new Exception("No channel with id " + channelId);
			}

			channel.RemoveListener(origin);
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