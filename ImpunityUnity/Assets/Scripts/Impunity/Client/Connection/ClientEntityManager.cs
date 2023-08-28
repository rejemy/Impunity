using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using UltraLiteDB;

using Impunity.GameState;

namespace Impunity.Connection
{
	[AttributeUsage(AttributeTargets.Class)]
	public class DistributedEntity : Attribute
	{
		internal int EntityId;
		public string FactoryMethod { get; set; }

		public DistributedEntity(int entityId)
		{
			EntityId = entityId;
		}
	}

	[AttributeUsage(AttributeTargets.Field)]
	public class Distributed : Attribute
	{
		internal int FieldId;
		public string OnChanged { get; set; }

		public Distributed(int fieldId)
		{
			FieldId = fieldId;
		}
	}

	public interface IDistributedField
	{
		GameStateEntityPropertyValueType ValueType { get; }

		void WriteChangesTo(BinaryWriter w);
		void ReadChangesFrom(BinaryReader r);
	}

	public struct DistributedValue<T> : IDistributedField where T : struct, IDistributableValueType
	{
		T CurrentValue;
		T NewValue;

		public void Set(T newValue)
		{
			NewValue = newValue;
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			NewValue.WriteChangesTo(w);
		}

		public void ReadChangesFrom(BinaryReader r)
		{
			//T oldValue = CurrentValue;
			CurrentValue.ReadChangesFrom(r);
			NewValue = default(T);
			//notify?.Invoke(oldValue, CurrentValue);
		}

		public GameStateEntityPropertyValueType ValueType { get => CurrentValue.ValueType; }

		public static implicit operator T(DistributedValue<T> d) => d.CurrentValue;
	}

	public interface IDistributedEntity
	{
		uint DistributedEntityId { get; set; }
		int DistributedEntityType { get; set; }
		ClientEntityManager Manager { get; set; }
		ulong DirtyBits { get; }

		void SetDirty(int fieldId);
		void ClearDirty();
	}

	public interface IDistributedChannel : IDistributedEntity
	{
		string ChannelName { get; set; }
	}

	public abstract class DistributedEntityBase : IDistributedEntity
	{
		public uint DistributedEntityId { get; set; }
		public int DistributedEntityType { get; set; }

		public ClientEntityManager Manager { get; set; }

		public ulong DirtyBits { get; private set; }
		public void SetDirty(int fieldId)
        {
			DirtyBits |= 1ul << (fieldId - 1);
		}

		public void ClearDirty()
        {
			DirtyBits = 0ul;
		}
	}

	public abstract class DistributedChannelBase : DistributedEntityBase, IDistributedChannel
	{
		public string ChannelName { get; set; }
	}

	public class GenericDistributedChannel : DistributedChannelBase
	{
	}


	// Internal type info

	public class DistributedTypeFieldInfo
	{
		public int FieldId;
		public FieldInfo FieldAccessor;
		public GameStateEntityPropertyValueType FieldValueType;
	}

	public class DistributedTypeInfo
	{
		public int DistributedTypeId;
		public bool IsChannel;
		public Type ObjectType;
		public Func<IDistributedEntity> Factory;
		public DistributedTypeFieldInfo[] DistributedFields;
	}


	public class ClientEntityManager
	{
		public BaseGameConnection Connection;

		private DistributedTypeInfo[] DistributedTypes;

		private Dictionary<uint, IDistributedEntity> DistributedObjects;
		private HashSet<IDistributedEntity> DirtyObjects;

		public ClientEntityManager()
        {
			DistributedTypes = null;
			DistributedObjects = new Dictionary<uint, IDistributedEntity>();
			DirtyObjects = new HashSet<IDistributedEntity>();
		}

		public GameStateEntityType[] RegisterEntityTypes(Type[] entityTypes)
        {
			if (entityTypes == null || entityTypes.Length == 0)
            {
				DistributedTypes = new DistributedTypeInfo[1];
				return null;
            }

			List<DistributedTypeInfo> internalTypeInfoList = new List<DistributedTypeInfo>();

			GameStateEntityType[] convertedEntityTypes = new GameStateEntityType[entityTypes.Length];


			int i = 0;
			foreach(Type entityType in entityTypes)
            {
				GameStateEntityType entityData = RegisterEntityType(entityType, internalTypeInfoList);
				convertedEntityTypes[i++] = entityData;
			}

			Array.Sort(convertedEntityTypes,
				(e1, e2) =>
				{
					return e1.Index - e2.Index;
				}
			);

			int maxEntityIndex = convertedEntityTypes[convertedEntityTypes.Length - 1].Index;

			DistributedTypes = new DistributedTypeInfo[maxEntityIndex + 1];
			foreach(DistributedTypeInfo dtinfo in internalTypeInfoList)
            {
				if (DistributedTypes[dtinfo.DistributedTypeId] != null)
                {
					throw new Exception("More than one type using distributed id " + dtinfo.DistributedTypeId);
                }

				DistributedTypes[dtinfo.DistributedTypeId] = dtinfo;
			}


			return convertedEntityTypes;
        }

		private GameStateEntityType RegisterEntityType(Type entityType, List<DistributedTypeInfo> internalTypeInfoList)
        {
			GameStateEntityType entityData = new GameStateEntityType();
			entityData.Name = entityType.Name;

			DistributedEntity distAttr = (DistributedEntity)entityType.GetCustomAttribute(typeof(DistributedEntity));
			if (distAttr == null)
			{
				throw new Exception("Tried to register distributed entity type " + entityType.Name + " with no DistributedEntity attribute");
			}

			entityData.Index = distAttr.EntityId;
			if(entityData.Index <= 0)
            {
				throw new Exception("Entity ID must be positive indeger");
            }

			DistributedTypeInfo internalTypeInfo = new DistributedTypeInfo();
			internalTypeInfo.DistributedTypeId = entityData.Index;
			internalTypeInfo.ObjectType = entityType;

			if (distAttr.FactoryMethod != null)
			{
				MethodInfo factoryMethod = entityType.GetMethod(distAttr.FactoryMethod, BindingFlags.Public | BindingFlags.Static);
				if (factoryMethod == null)
				{
					throw new Exception("No public static factory method " + distAttr.FactoryMethod + " for type " + entityType.Name);
				}

				internalTypeInfo.Factory = (Func<IDistributedEntity>)factoryMethod.CreateDelegate(typeof(Func<IDistributedEntity>), null);
			}

			if (entityType.GetInterface(nameof(IDistributedChannel)) != null)
			{
				internalTypeInfo.IsChannel = true;
			}
			else if (entityType.GetInterface(nameof(IDistributedEntity)) == null)
			{
				throw new Exception("Distributed entity class " + entityType.Name + " doesn't implement IDistributedEntity");
			}

			List<DistributedTypeFieldInfo> distributedFields = new List<DistributedTypeFieldInfo>();

			foreach (var fieldInfo in entityType.GetRuntimeFields())
			{
				if (fieldInfo.IsStatic)
				{
					continue;
				}

				Distributed fieldAttr = (Distributed)fieldInfo.GetCustomAttribute(typeof(Distributed));
				if (fieldAttr == null)
				{
					continue;
				}

				if (fieldAttr.FieldId <= 0 || fieldAttr.FieldId >= 64)
				{
					throw new Exception("Field ID must be positive indeger under 64");
				}

				Type fieldType = fieldInfo.FieldType;
				if (fieldType.GetInterface(nameof(IDistributedField)) == null)
				{
					throw new Exception("Distributed fields must implement IDistributedField");
				}

				// Create a throw-away instance so we can get its type info
				IDistributedField tempFieldValue = (IDistributedField)Activator.CreateInstance(fieldType);

				DistributedTypeFieldInfo dfield = new DistributedTypeFieldInfo();
				dfield.FieldId = fieldAttr.FieldId;
				dfield.FieldAccessor = fieldInfo;
				dfield.FieldValueType = tempFieldValue.ValueType;
				distributedFields.Add(dfield);
			}

			if(distributedFields.Count == 0)
            {
				internalTypeInfoList.Add(internalTypeInfo);
				return entityData;
			}

			entityData.Properties = new GameStateEntityPropertyDef[distributedFields.Count];

			int p = 0;
			foreach (DistributedTypeFieldInfo dfield in distributedFields)
            {
				GameStateEntityPropertyDef propDef = new GameStateEntityPropertyDef();
				propDef.Index = dfield.FieldId;
				propDef.Name = dfield.FieldAccessor.Name;
				propDef.PropValueType = (byte)dfield.FieldValueType;
				entityData.Properties[p++] = propDef;
			}

			Array.Sort(entityData.Properties,
				(e1, e2) =>
				{
					return e1.Index - e2.Index;
				}
			);

			int maxFieldIndex = entityData.Properties[entityData.Properties.Length - 1].Index;

			internalTypeInfo.DistributedFields = new DistributedTypeFieldInfo[maxFieldIndex + 1];
			foreach (DistributedTypeFieldInfo dfield in distributedFields)
			{
				if (internalTypeInfo.DistributedFields[dfield.FieldId] != null)
				{
					throw new Exception("More than one distributed field using id " + dfield.FieldId);
				}

				internalTypeInfo.DistributedFields[dfield.FieldId] = dfield;
			}

			internalTypeInfoList.Add(internalTypeInfo);
			return entityData;
		}

		private DistributedTypeInfo GetDistributedTypeInfo(int typeId)
        {
			if (typeId <= 0 || typeId >= DistributedTypes.Length)
            {
				throw new Exception("Invalid distributed type id: " + typeId);
            }

			DistributedTypeInfo typeInfo = DistributedTypes[typeId];
			if (typeInfo == null)
            {
				throw new Exception("Unknown distributed type id: " + typeId);
            }

			return typeInfo;
		}

		public void HandleCreateChannel(uint channelId, string channelName, int channelType)
		{
			IDistributedChannel channel = null;
			if (channelType != 0)
            {
				DistributedTypeInfo typeInfo = GetDistributedTypeInfo(channelType);
				if (typeInfo.IsChannel == false)
                {
					throw new Exception("Tried to create channel of type " + channelType + " but " + typeInfo.ObjectType.Name + " doesn't implement IDistributedChannel");
                }

				if (typeInfo.Factory != null)
                {
					channel = (IDistributedChannel)typeInfo.Factory();
				}
				else
                {
					channel = (IDistributedChannel)Activator.CreateInstance(typeInfo.ObjectType);
				}
			}
			else
            {
				channel = new GenericDistributedChannel();
			}

			channel.ChannelName = channelName;
			channel.DistributedEntityId = channelId;
			channel.Manager = this;

			DistributedObjects[channelId] = channel;
		}

		public void HandleCreateObject(uint objectId, uint channelId, int objectType)
		{
			IDistributedEntity entity = null;

			DistributedTypeInfo typeInfo = DistributedTypes[objectType];
			if (typeInfo.Factory != null)
			{
				entity = typeInfo.Factory();
			}
			else
			{
				entity = (IDistributedEntity)Activator.CreateInstance(typeInfo.ObjectType);
			}

			entity.DistributedEntityId = objectId;
			entity.Manager = this;

			DistributedObjects[objectId] = entity;
		}

		public void HandleEntityUpdate(uint entityId)
		{
			ImpunityLogger.LogInformation("Got entity update request");
		}

		public void HandleEntityEvent(uint entityId, int eventType, BsonValue eventData)
		{
			ImpunityLogger.LogInformation("Got entity event request");
		}

		public void HandleEntityDelete(uint entityId)
		{
			ImpunityLogger.LogInformation("Got entity delete request");
		}

		public void SetDirty(IDistributedEntity entity)
        {
			DirtyObjects.Add(entity);

		}

		public void SendUpdates()
        {
			foreach (IDistributedEntity entity in DirtyObjects)
            {
				SendEntityUpdates(entity);
			}

			DirtyObjects.Clear();

		}

		private void SendEntityUpdates(IDistributedEntity entity)
        {
			DistributedTypeInfo typeInfo = DistributedTypes[entity.DistributedEntityType];

			for(int i=0; i<typeInfo.DistributedFields.Length; i++)
            {

            }

			entity.ClearDirty();

		}
	}

}