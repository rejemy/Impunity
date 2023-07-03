using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.Concurrent;

using UltraLiteDB;


namespace Impunity.GameState
{

    // One window into the game state, maps to a single computer, either local or over network
    // Not necessarily a single player/user
    public class GameStateReplicant
    {
        Dictionary<uint, GameStateEntityBase> LocksHeld;

        public GameStateReplicant()
        {

        }
    }

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
    }

    // Entity that exists only to be a lock, will be deleted when lock is released
    public class GameStateEntityLock : GameStateEntityBase
    {
        public GameStateEntityLock(string name) : base (name)
        {
            
        }
    }

    // One "universe" of game state entities
    public class GameStateEntities
    {
        GameStateDB DB;
        Dictionary<uint, GameStateEntityBase> AllEntities;
        Dictionary<string, GameStateEntityBase> NamedEntities;

        uint NextId;

        public GameStateEntities(GameStateDB db)
        {
            DB = db;
            AllEntities = new Dictionary<uint, GameStateEntityBase>();
            NamedEntities = new Dictionary<string, GameStateEntityBase>();
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

        public bool TryToLock(string key, string name)
        {
            GameStateEntityBase entity = NamedEntities[name];
            if (entity == null)
            {
                entity = new GameStateEntityLock(name);
                RegisterEntity(entity);
            }

            return entity.Lock(key);
        }

        public bool Unlock(string key, string name)
        {
            GameStateEntityBase entity = NamedEntities[name];
            if (entity == null)
            {
                return false;
            }

            bool unlocked = entity.Unlock(key);
            if (unlocked && entity is GameStateEntityLock)
            {
                UnregisterEntity(entity);
            }

            return unlocked;
        }
    }
}