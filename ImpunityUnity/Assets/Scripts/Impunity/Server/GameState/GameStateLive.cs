using System;
using System.Collections.Generic;

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
                entity.Destroy();
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

        /*
        public void SendChannelCreation(GameStateChannel channel)
        {

        }

        public void SendObjectCreation(GameStateObject obj)
        {
            if (DistributedObjects.ContainsKey(obj.Id))
            {
                return;
            }

            DistributedObjects[obj.Id] = obj;

            // Send to client
        }
        */
    }

    // Base for all live entities
    public abstract class GameStateEntity
    {
        public uint Id { get; internal set; }
        public string Name { get; private set; } // Not all entities have names

        protected GameStateEntityType TypeInfo;

        public GameStateReplicant LockHeldBy;
        public string LockedWith { get; internal set; }
        public GameStateReplicant EphemeralOwner;

        public GameStateEntity(GameStateEntityType typeInfo, string name = null)
        {
            TypeInfo = typeInfo;
            Name = name;
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

        public virtual void UpdateProps()
        {

        }

        public virtual void Destroy()
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

        public GameStateChannel(GameStateEntityType typeInfo, string name) : base(typeInfo, name)
        {
            Members = new Dictionary<uint, GameStateObject>();
            Listeners = new Dictionary<string, GameStateReplicant>();
        }

        public void AddListener(GameStateReplicant replicant)
        {
            Listeners.Add(replicant.Id, replicant);

            ChannelCreateMessageAction channelCreate = new ChannelCreateMessageAction();
            channelCreate.ChannelId = Id;
            channelCreate.ChannelName = Name;
            channelCreate.ChannelType = TypeInfo.Index;
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

        public void AddObject(GameStateObject obj)
        {
            Members.Add(obj.Id, obj);

            ObjectCreateMessageAction createMessage = obj.MakeCreateMessage();

            SendToListeners(createMessage);
        }

        public void RemoveObject(GameStateObject obj)
        {
            Members.Remove(obj.Id);
        }

        public override void UpdateProps()
        {
            base.UpdateProps();

            EntityUpdateMessageAction updateMessage = new EntityUpdateMessageAction();
            updateMessage.EntityId = Id;

            SendToListeners(updateMessage);
        }

        public override void Destroy()
        {
            EntityDeleteMessageAction deleteMessage = new EntityDeleteMessageAction();
            deleteMessage.EntityId = Id;

            SendToListeners(deleteMessage);

            Members.Clear();
            Listeners.Clear();

            base.Destroy();


        }

        public void SendToListeners(ServerActionBase message)
        {
            foreach (GameStateReplicant replicant in Listeners.Values)
            {
                replicant.SendMessageViaConnection(message);
            }
        }
    }

    public class GameStateObject : GameStateEntity
    {
        GameStateChannel Channel;

        public GameStateObject(GameStateEntityType typeInfo, GameStateChannel channel) : base(typeInfo, null)
        {
            Channel = channel;
        }

        public ObjectCreateMessageAction MakeCreateMessage()
        {
            ObjectCreateMessageAction message = new ObjectCreateMessageAction();
            message.ObjectId = Id;
            message.ChannelId = Channel.Id;
            message.ObjectType = TypeInfo.Index;

            return message;
        }

        public override void UpdateProps()
        {
            base.UpdateProps();

            EntityUpdateMessageAction updateMessage = new EntityUpdateMessageAction();
            updateMessage.EntityId = Id;

            Channel.SendToListeners(updateMessage);
        }

        public override void Destroy()
        {
            EntityDeleteMessageAction deleteMessage = new EntityDeleteMessageAction();
            deleteMessage.EntityId = Id;
            Channel.RemoveObject(this);

            Channel.SendToListeners(deleteMessage);
            Channel = null;

            base.Destroy();
        }
    }

    // Entity that exists only to be a lock, will be deleted when lock is released
    public class GameStateNamedLock : GameStateEntity
    {
        public GameStateNamedLock(string name) : base (null, name)
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

        public GameStateLive(GameStateDB db)
        {
            DB = db;
            AllEntities = new Dictionary<uint, GameStateEntity>();
            NamedEntities = new Dictionary<string, GameStateEntity>();
            ConnectedReplicas = new HashSet<GameStateReplicant>();
        }

        public void EnsureFormat(GameStateFormat format)
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

        private void DestroyEntity(GameStateEntity entity)
        {
            UnregisterEntity(entity);
            entity.Destroy();
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

        // ----- Public API below


        public uint CreateChannel(GameStateReplicant origin, int typeId, string name)
        {
            if (name == null)
            {
                throw new Exception("Name must be set for channel");
            }

            if (NamedEntities.ContainsKey(name))
            {
                throw new Exception("Entity with name " + name + " already exists");
            }

            GameStateEntityType typeInfo = null;
            if (typeId != 0)
            {
                typeInfo = GetEntityType(typeId);
            }

            GameStateChannel channel = new GameStateChannel(typeInfo, name);
            RegisterEntity(channel);

            channel.AddListener(origin);
            origin.AddSubscribedChannel(channel);

            return channel.Id;
        }

        public uint CreateObject(GameStateReplicant origin, int typeId, uint channelId)
        {
            GameStateEntityType typeInfo = GetEntityType(typeId);

            GameStateChannel channel = AllEntities[channelId] as GameStateChannel;
            if (channel == null)
            {
                throw new Exception("No channel with ID " + channelId);
            }

            GameStateObject dobj = new GameStateObject(typeInfo, channel);
            RegisterEntity(dobj);

            // Will notify all listeners of new object
            channel.AddObject(dobj);

            return dobj.Id;
        }

        public bool UpdateEntity(uint entityId)
        {
            GameStateEntity entity = AllEntities[entityId];
            if (entity == null)
            {
                throw new Exception("No entity with ID " + entityId);
            }

            if (entity.IsLocked())
            {
                // Can't update locked entity
                return false;
            }

            entity.UpdateProps();


            return true;
        }

        public void SendEntityEvent(uint entityId, int eventType, BsonValue eventData)
        {
            GameStateEntity entity = AllEntities[entityId];
            if (entity == null)
            {
                throw new Exception("No entity with ID " + entityId);
            }

        }

        public bool DeleteEntity(uint entityId)
        {
            GameStateEntity entity = AllEntities[entityId];
            if (entity == null)
            {
                throw new Exception("No entity with ID " + entityId);
            }

            if (entity.IsLocked())
            {
                // Can't delete locked entity
                return false;
            }

            DestroyEntity(entity);

            return true;
        }

        public bool LockEntity(GameStateReplicant origin, uint entityId, string key)
        {
            GameStateEntity entity = AllEntities[entityId];
            if (entity == null)
            {
                throw new Exception("No entity with ID " + entityId);
            }

            if (entity.IsLocked())
            {
                // Already locked
                return false;
            }

            entity.Lock(key, origin);

            return true;
        }

        public bool UnlockEntity(GameStateReplicant origin, uint entityId, string key)
        {
            GameStateEntity entity = AllEntities[entityId];
            if (entity == null)
            {
                throw new Exception("No entity with ID " + entityId);
            }

            return entity.Unlock(key, origin);
        }

        public bool TryToLockNamedLock(GameStateReplicant origin, string name, string key)
        {
            GameStateEntity entity = NamedEntities[name];
            if (entity == null)
            {
                // Create placeholder named lock object if no entity with that name exists
                entity = new GameStateNamedLock(name);
                RegisterEntity(entity);
                origin.AddEphemeralEntity(entity);
            }

            bool locked = entity.Lock(key, origin);
            
            return locked;
        }

        public bool UnlockNamedLock(GameStateReplicant origin, string name, string key)
        {
            GameStateEntity entity = NamedEntities[name];
            if (entity == null)
            {
                return false;
            }

            bool unlocked = entity.Unlock(key, origin);
            if (unlocked)
            {
                if (entity is GameStateNamedLock)
                {
                    DestroyEntity(entity);
                }
            }

            return unlocked;
        }

        public uint SubscribeToChannel(GameStateReplicant origin, string channelName)
        {
            GameStateChannel channel = NamedEntities[channelName] as GameStateChannel;
            if (channel == null)
            {
                throw new Exception("No channel with name " + channelName);
            }

            channel.AddListener(origin);

            return channel.Id;
        }

        public void UnsubscribeFromChannel(GameStateReplicant origin, uint channelId)
        {
            GameStateChannel channel = AllEntities[channelId] as GameStateChannel;
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