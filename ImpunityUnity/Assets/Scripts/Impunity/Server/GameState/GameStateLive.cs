
using System.Collections.Generic;

using UltraLiteDB;

using Impunity.Networking;

namespace Impunity.GameState
{

    // One window into the game state, maps to a single computer, either local or over network
    // Not necessarily a single player/user
    public class GameStateReplicant
    {
        public IServerSideConnectionProxy ConnectionProxy;
        public Dictionary<string, GameStateEntityBase> LocksHeld;

        public GameStateReplicant(IServerSideConnectionProxy proxy)
        {
            ConnectionProxy = proxy;
            LocksHeld = new Dictionary<string, GameStateEntityBase>();
        }

        public void Cleanup()
        {
            foreach (GameStateEntityBase entity in LocksHeld.Values)
            {
                entity.Unlock();
            }

            LocksHeld.Clear();
        }

        public void SendMessageViaConnection(ServerActionBase message)
        {
            ConnectionProxy.SendMessageToClient(message);
        }
    }

    // Base for all live entities
    public abstract class GameStateEntityBase
    {
        public uint Id { get; internal set; }
        public string Name { get; private set; } // Not all entities have names
        string LockedBy;

        public GameStateEntityBase(string name)
        {
            Name = name;
        }

        public bool Lock(string key)
        {
            if (LockedBy != null && LockedBy != key)
            {
                return false;
            }

            LockedBy = key;
            return true;
        }

        public bool Unlock(string key)
        {
            if (LockedBy != key)
            {
                return false;
            }

            LockedBy = null;
            return true;
        }

        public void Unlock()
        {
            LockedBy = null;
        }

    }

    // Entity that exists only to be a lock, will be deleted when lock is released
    public class GameStateEntityLock : GameStateEntityBase
    {
        public GameStateEntityLock(string name) : base (name)
        {
            
        }
    }

    // Live gamestate in memory
    public class GameStateLive
    {
        GameStateDB DB;
        Dictionary<uint, GameStateEntityBase> AllEntities;
        Dictionary<string, GameStateEntityBase> NamedEntities;

        HashSet<GameStateReplicant> ConnectedReplicas;

        uint NextId;

        public GameStateLive(GameStateDB db)
        {
            DB = db;
            AllEntities = new Dictionary<uint, GameStateEntityBase>();
            NamedEntities = new Dictionary<string, GameStateEntityBase>();
            ConnectedReplicas = new HashSet<GameStateReplicant>();
        }

        public void AddGameStateReplicant(GameStateReplicant replica)
        {
            ConnectedReplicas.Add(replica);
        }

        public void RemoveGameStateReplicant(GameStateReplicant replica)
        {
            ConnectedReplicas.Remove(replica);
        }

        uint FindAvailableEntityId()
        {
            return NextId++;
        }

        public void RegisterEntity(GameStateEntityBase entity)
        {
            entity.Id = FindAvailableEntityId();
            AllEntities[entity.Id] = entity;
            if (entity.Name != null)
            {
                NamedEntities[entity.Name] = entity;
            }

        }

        public void UnregisterEntity(GameStateEntityBase entity)
        {
            AllEntities.Remove(entity.Id);
            if (entity.Name != null)
            {
                NamedEntities.Remove(entity.Name);
            }
        }

        public bool TryToLock(GameStateReplicant origin, string name, string key)
        {
            GameStateEntityBase entity = NamedEntities[name];
            if (entity == null)
            {
                entity = new GameStateEntityLock(name);
                RegisterEntity(entity);
            }

            bool locked = entity.Lock(key);
            if (locked)
            {
                origin.LocksHeld[name] = entity;
            }

            return locked;
        }

        public bool Unlock(GameStateReplicant origin, string name, string key)
        {
            GameStateEntityBase entity = NamedEntities[name];
            if (entity == null)
            {
                return false;
            }

            bool unlocked = entity.Unlock(key);
            if (unlocked)
            {
                if (entity is GameStateEntityLock)
                {
                    UnregisterEntity(entity);
                }

                origin.LocksHeld.Remove(name);
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