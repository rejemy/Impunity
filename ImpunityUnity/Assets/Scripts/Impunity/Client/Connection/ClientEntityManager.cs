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

		public bool Set(T newValue)
		{
			NewValue = newValue;
			return !NewValue.Equals(CurrentValue);
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			NewValue.WriteTo(w);
		}

		public void ReadChangesFrom(BinaryReader r)
		{
			CurrentValue.ReadFrom(r);
			NewValue = CurrentValue;
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
			Manager?.SetDirty(this);
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

		private byte[] PropertyEncodingBuffer;
		private BinaryWriter PropertyEncodingWriter;

		public ClientEntityManager()
        {
			DistributedTypes = null;
			DistributedObjects = new Dictionary<uint, IDistributedEntity>();
			DirtyObjects = new HashSet<IDistributedEntity>();

			PropertyEncodingBuffer = new byte[ImpunityConstants.MaxMessageSize];
			PropertyEncodingWriter = new BinaryWriter(new MemoryStream(PropertyEncodingBuffer));
		}

		// -------------- Public API

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

		public void CreateChannel<T>(T channel, string name, ImpunityCallback<T> onComplete) where T : IDistributedChannel
		{
			if (name == null)
            {
				throw new Exception("Channel must have name");
            }

			channel.ChannelName = name;

			Type entityType = channel.GetType();
			DistributedEntity distAttr = (DistributedEntity)entityType.GetCustomAttribute(typeof(DistributedEntity));
			if (distAttr == null)
			{
				throw new Exception("Tried to create distributed channel type " + entityType.Name + " with no DistributedEntity attribute");
			}

			int entityTypeId = distAttr.EntityId;
			if (entityTypeId <= 0 || entityTypeId >= DistributedTypes.Length || DistributedTypes[entityTypeId] == null)
            {
				throw new Exception("Tried to create distributed channel with invalid entity type id: " + entityTypeId);
            }

			channel.DistributedEntityType = entityTypeId;
			byte[] propertyBytes = GetPropertyBytes(channel);

			Connection.CreateChannel(entityTypeId, channel.ChannelName, null, (ImpunityError err, uint channelId) =>
			{
				RegisterEntity(channel, channelId);
				onComplete?.Invoke(err, channel);
			});
		}

		public void CreateObject<T>(T distObj, IDistributedChannel channel, ImpunityCallback<T> onComplete) where T : IDistributedEntity
		{

			Type entityType = distObj.GetType();
			DistributedEntity distAttr = (DistributedEntity)entityType.GetCustomAttribute(typeof(DistributedEntity));
			if (distAttr == null)
			{
				throw new Exception("Tried to create distributed object type " + entityType.Name + " with no DistributedEntity attribute");
			}

			int entityTypeId = distAttr.EntityId;
			if (entityTypeId <= 0 || entityTypeId >= DistributedTypes.Length || DistributedTypes[entityTypeId] == null)
			{
				throw new Exception("Tried to create distributed object with invalid entity type id: " + entityTypeId);
			}

			distObj.DistributedEntityType = entityTypeId;
			byte[] propertyBytes = GetPropertyBytes(distObj);

			Connection.CreateObject(entityTypeId, channel.DistributedEntityId, null, (ImpunityError err, uint objectId) =>
			{
				RegisterEntity(distObj, objectId);
				onComplete?.Invoke(err, distObj);
			});
		}

		// ---------------

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

		public void HandleCreateChannel(uint channelId, string channelName, int channelType, byte[] propData)
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
			RegisterEntity(channel, channelId);
		}

		private void RegisterEntity(IDistributedEntity entity, uint entityId)
        {
			entity.DistributedEntityId = entityId;
			entity.Manager = this;

			DistributedObjects[entity.DistributedEntityId] = entity;

			if (entity.DirtyBits != 0)
			{
				SetDirty(entity);
			}
		}

		public void HandleCreateObject(uint objectId, uint channelId, int objectType, byte[] propData)
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

			RegisterEntity(entity, objectId);
		}

		public void HandleEntityUpdate(uint entityId, byte[] updateData)
		{
			ImpunityLogger.LogInformation("Got entity update request");
		}

		public void HandleEntityEvent(uint entityId, int eventType, BsonValue eventData)
		{
			ImpunityLogger.LogInformation("Got entity event request");
		}

		public void HandleEntityDelete(uint entityId, BsonValue deleteData)
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

		private byte[] GetPropertyBytes(IDistributedEntity entity)
        {
			DistributedTypeInfo typeInfo = DistributedTypes[entity.DistributedEntityType];

			ulong dirtyBits = entity.DirtyBits;
			if (dirtyBits == 0)
            {
				return null;
			}

			PropertyEncodingWriter.BaseStream.Position = 0;
			foreach (var fieldInfo in typeInfo.DistributedFields)
			{
				if (fieldInfo == null) continue;

				if ((dirtyBits & (1ul << (fieldInfo.FieldId - 1))) != 0)
				{
					PropertyEncodingWriter.Write((byte)fieldInfo.FieldId);
					// Boxes a copy :(
					IDistributedField fieldInst = (IDistributedField)fieldInfo.FieldAccessor.GetValue(entity);
					fieldInst.WriteChangesTo(PropertyEncodingWriter);
				}
			}

			PropertyEncodingWriter.Write((byte)0);

			entity.ClearDirty();

			byte[] updateDatabuffer = PropertyEncodingBuffer;

			// Makes new array and assigns it to updateDataBuffer, unfortunately causing an allocation for each property update.
			// to be fixed by rewriting the entire action serialization system
			Array.Resize<byte>(ref updateDatabuffer, (int)PropertyEncodingWriter.BaseStream.Position);
			PropertyEncodingWriter.BaseStream.Position = 0;

			return updateDatabuffer;
		}

		private void SendEntityUpdates(IDistributedEntity entity)
        {

			byte[] updateDatabuffer = GetPropertyBytes(entity);

			Connection.UpdateEntity(entity.DistributedEntityId, null, updateDatabuffer, null);

		}
	}

}