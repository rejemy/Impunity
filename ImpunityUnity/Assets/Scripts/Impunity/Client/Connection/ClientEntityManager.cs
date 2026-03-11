using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Impunity.GameState;
using UltraLiteDB;


namespace Impunity.Connection
{
	/// <summary>Marks a class as a distributed entity type with a unique integer ID. Optionally specifies a factory method and persistence key.</summary>
	[AttributeUsage(AttributeTargets.Class)]
	public class DistributedEntity : Attribute
	{
		internal int EntityId;
		public string FactoryMethod { get; set; }
		public string PersistAs { get; set; }

		public DistributedEntity(int entityId)
		{
			EntityId = entityId;
		}
	}

	/// <summary>Marks a field as a distributed property with a unique byte ID (1-63). Optionally specifies a persistence key.</summary>
	[AttributeUsage(AttributeTargets.Field)]
	public class Distributed : Attribute
	{
		internal byte FieldId;
		public string PersistAs { get; set; }

		public Distributed(byte fieldId)
		{
			FieldId = fieldId;
		}
	}


	/// <summary>Client-side interface for a distributed entity. Provides identity, dirty tracking, lock/event/delete operations, and lifecycle callbacks.</summary>
	public interface IDistributedEntity
	{
		string Name { get; set; }
		uint DistributedEntityId { get; set; }
		int DistributedEntityType { get; set; }
		bool IsClientAuthoritative { get; set; }
		bool IsPersisted { get; set; }
		bool IsLocked { get; set; }

		ClientEntityManager Manager { get; set; }

		ulong DirtyBits { get; }

		void SetDirty(byte fieldId);
		void ClearDirty();

		void InitializeDistributedFields();

		void TriggerEvent(int eventType, BsonValue eventData, ImpunityCallback onComplete);
		void Delete(BsonValue deleteData, ImpunityCallback<bool> onComplete);
		void TryLock(ImpunityCallback<bool> onComplete);
		void WaitForLock(ImpunityCallback<LockWaitResult> onComplete);
		void Unlock(ImpunityCallback<bool> onComplete);

		void OnFullyInitialized();
		void OnEventTriggered(int eventType, BsonValue eventData);
		void OnDeleted(BsonValue deleteData);
		void OnLocked();
		void OnUnlocked();
		void OnUndistributed();
	}

	/// <summary>Client-side interface for a distributed channel entity. Channels contain child objects and support subscription.</summary>
	public interface IDistributedChannel : IDistributedEntity
	{
		Dictionary<uint, IDistributedEntity> DistributedObjects { get; }

		void Unsubscribe(ImpunityCallback onComplete);

		void OnObjectAdded(IDistributedEntity entity, bool newlyCreated);
		void OnObjectRemoved(uint entityId, bool destroyed);
	}

	/// <summary>Base implementation of <see cref="IDistributedEntity"/> with dirty-bit tracking, lock waiting, and lifecycle callback stubs.</summary>
	public abstract class DistributedEntityBase : IDistributedEntity
	{
		public string Name { get; set; }
		public uint DistributedEntityId { get; set; }
		public int DistributedEntityType { get; set; }
		public bool IsClientAuthoritative { get; set; }
		public bool IsPersisted { get; set; }
		public bool IsLocked { get; set; }
		private event ImpunityCallback<LockWaitResult> LockWaiter;

		public ClientEntityManager Manager { get; set; }
		public IDistributedChannel Channel { get; set; }

		public ulong DirtyBits { get; private set; }
		public void SetDirty(byte fieldId)
        {
			DirtyBits |= 1ul << (fieldId - 1);
			Manager?.SetDirty(this);
		}

		public void ClearDirty()
        {
			DirtyBits = 0ul;
		}

		public virtual void InitializeDistributedFields() {}

		public void TriggerEvent(int eventType, BsonValue eventData, ImpunityCallback onComplete)
		{
			Manager.Connection.TriggerEntityEvent(DistributedEntityId, eventType, eventData, onComplete);
		}

		public void Delete(BsonValue deleteData, ImpunityCallback<bool> onComplete)
		{
			Manager.Connection.DeleteEntity(DistributedEntityId, deleteData, onComplete);
		}

		public void TryLock(ImpunityCallback<bool> onComplete)
		{
			Manager.Connection.TryToLockEntity(DistributedEntityId, onComplete);
		}

		public void WaitForLock(ImpunityCallback<LockWaitResult> onComplete)
		{
			Manager.Connection.TryToLockEntity(DistributedEntityId, (err, lockResult) =>
			{
				if (err != null)
				{
					onComplete?.Invoke(err, LockWaitResult.Error);
				}
				else if(lockResult)
				{
					onComplete?.Invoke(err, LockWaitResult.Locked);
				}
				else
				{
					LockWaiter += onComplete;
				}
			});
		}


		public void Unlock(ImpunityCallback<bool> onComplete)
		{
			Manager.Connection.UnlockEntity(DistributedEntityId, onComplete);
		}
		
		public virtual void OnLocked()
		{
		}

		public virtual void OnUnlocked()
		{
			try
			{
				LockWaiter?.Invoke(null, LockWaitResult.Unlocked);
			}
			catch (Exception ex)
			{
				ImpunityLogger.LogError("Exception in entity WaitForLock callback handler:", ex);
			}
			LockWaiter = null;
		}

		public virtual void OnFullyInitialized() { }
		public virtual void OnEventTriggered(int eventType, BsonValue eventData) { }
		public virtual void OnDeleted(BsonValue deleteData) { }
		public virtual void OnUndistributed() { }
	}

	/// <summary>Base implementation of <see cref="IDistributedChannel"/>. Maintains a dictionary of child objects and supports unsubscription.</summary>
	public abstract class DistributedChannelBase : DistributedEntityBase, IDistributedChannel
	{
		public Dictionary<uint, IDistributedEntity> DistributedObjects { get; private set; } = new Dictionary<uint, IDistributedEntity>();

		public void Unsubscribe(ImpunityCallback onComplete)
		{
			Manager.UnsubscribeFromChannel(this, onComplete);
		}

		public virtual void OnObjectAdded(IDistributedEntity entity, bool newlyCreated)
		{
			DistributedObjects.Add(entity.DistributedEntityId, entity);
		}

		public virtual void OnObjectRemoved(uint entityId, bool destroyed)
        {
			DistributedObjects.Remove(entityId);
		}

	}

	/// <summary>Default channel type used when no custom channel class is specified (type ID 0).</summary>
	public class GenericDistributedChannel : DistributedChannelBase
	{
		public GenericDistributedChannel() : base()
		{
		}
	}


	// Internal type info

	/// <summary>Internal metadata for a single distributed field: ID, serialization methods, persistence info.</summary>
	public class DistributedTypeFieldInfo
	{
		public byte FieldId;
		public string FieldName;
		public string PersistedAs;
		public bool IsTemporal;
		public GameStateEntityFieldType FieldType;
		public GameStateEntityPropertyValueType FieldValueType;
		public MethodInfo WriteMethod;
		public MethodInfo InitMethod;
		public MethodInfo UpdateMethod;
	}

	/// <summary>Internal metadata for a distributed entity type: type ID, factory, field definitions, channel/persistence flags.</summary>
	public class DistributedTypeInfo
	{
		public int DistributedTypeId;
		public bool IsChannel;
		public bool Persisted;
		public Type ObjectType;
		public Func<IDistributedEntity> Factory;
		public DistributedTypeFieldInfo[] DistributedFields;
	}


	/// <summary>
	/// Client-side manager for distributed entities. Handles type registration, entity creation/subscription,
	/// dirty tracking, property serialization/deserialization, and dispatching server state updates to entity instances.
	/// </summary>
	public class ClientEntityManager
	{
		/// <summary>The connection this manager sends actions through.</summary>
		public BaseGameConnection Connection;

		private DistributedTypeInfo[] DistributedTypes;

		private Dictionary<string, IDistributedChannel> SubscribedChannels;
		private Dictionary<uint, IDistributedEntity> DistributedObjects;
		private HashSet<IDistributedEntity> DirtyObjects;

		private byte[] PropertyEncodingBuffer;
		private BinaryWriter PropertyEncodingWriter;
		private object[] WriteMethodArgs;

		private byte[] PropertyDecodingBuffer;
		private BinaryReader PropertyDecodingReader;
		private object[] UpdateMethodArgs;

		/// <summary>Called when a distributed object is created in any subscribed channel. Parameters: entity, parent channel, newly created flag.</summary>
		public Action<IDistributedEntity, IDistributedChannel, bool> OnDistributedObjectCreated;

		public ClientEntityManager()
        {
			DistributedTypes = null;
			SubscribedChannels = new Dictionary<string, IDistributedChannel>();
			DistributedObjects = new Dictionary<uint, IDistributedEntity>();
			DirtyObjects = new HashSet<IDistributedEntity>();

			PropertyEncodingBuffer = new byte[ImpunityConstants.MaxMessageSize];
			PropertyEncodingWriter = new BinaryWriter(new MemoryStream(PropertyEncodingBuffer));
			WriteMethodArgs = new object[] { PropertyEncodingWriter };

			PropertyDecodingBuffer = new byte[ImpunityConstants.MaxMessageSize];
			PropertyDecodingReader = new BinaryReader(new MemoryStream(PropertyDecodingBuffer));
			UpdateMethodArgs = new object[] { PropertyDecodingReader };
		}

		// -------------- Public API

		/// <summary>Registers all distributed entity types via reflection. Builds internal type metadata and returns format definitions for the server handshake.</summary>
		public GameStateEntityTypeDef[] RegisterEntityTypes(Type[] entityTypes)
        {
			if (entityTypes == null || entityTypes.Length == 0)
            {
				DistributedTypes = new DistributedTypeInfo[1];
				return null;
            }

			List<DistributedTypeInfo> internalTypeInfoList = new List<DistributedTypeInfo>();

			GameStateEntityTypeDef[] convertedEntityTypes = new GameStateEntityTypeDef[entityTypes.Length];


			int i = 0;
			foreach(Type entityType in entityTypes)
            {
				GameStateEntityTypeDef entityData = RegisterEntityType(entityType, internalTypeInfoList);
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


		/// <summary>Creates a distributed object in a channel. Serializes initial properties and sends to the server. Returns the entity via callback after server confirmation.</summary>
		public void CreateObject<T>(T distObj, IDistributedChannel channel, bool replace, ImpunityCallback<T> onComplete) where T : class, IDistributedEntity
		{
			if(distObj.Name != null && distObj.Name.Contains("/"))
			{
				throw new Exception("Object name cannot contain forward slash");
			}

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
			ArraySegment<byte> propertyBytes = GetPropertyBytes(distObj);

			byte instaceFlags = 0;
			if (distObj.IsClientAuthoritative)
			{
				instaceFlags |= (byte)ImpunityInstanceFlags.ClientAuthoritative;

				if (DistributedTypes[entityTypeId].Persisted)
				{
					throw new Exception("Can't create a client authoritative object that is also persisted");
				}
			}
			if (distObj.IsPersisted)
			{
				if (!DistributedTypes[entityTypeId].Persisted)
				{
					throw new Exception("Can't create persisted object of a type that is not persistant");
				}

				if (!channel.IsPersisted)
				{
					throw new Exception("Unable to create persisted object in non-persisted channel");
				}

				instaceFlags |= (byte)ImpunityInstanceFlags.Persisted;
			}

			Connection.CreateObject(entityTypeId, instaceFlags, channel.DistributedEntityId, propertyBytes, distObj.Name, replace, (ImpunityErrorResponse err, uint objectId) =>
			{
				if (err != null)
				{
					onComplete?.Invoke(err, null);
					return;
				}

				RegisterEntity(distObj, objectId);
				onComplete?.Invoke(err, distObj);
			});
		}

		private ObjectCreateData MakeObjectCreateData(IDistributedEntity distObj, IDistributedChannel channel)
		{
			ObjectCreateData data = new ObjectCreateData();

			if(distObj.Name != null && distObj.Name.Contains("/"))
			{
				throw new Exception("Object name cannot contain forward slash");
			}

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
			ArraySegment<byte> propertyBytes = GetPropertyBytes(distObj);

			byte instanceFlags = 0;
			if (distObj.IsClientAuthoritative)
			{
				instanceFlags |= (byte)ImpunityInstanceFlags.ClientAuthoritative;

				if (DistributedTypes[entityTypeId].Persisted)
				{
					throw new Exception("Can't create a client authoritative object that is also persisted");
				}
			}
			if (distObj.IsPersisted)
			{
				if (!DistributedTypes[entityTypeId].Persisted)
				{
					throw new Exception("Can't create persisted object of a type that is not persistant");
				}

				if (!channel.IsPersisted)
				{
					throw new Exception("Unable to create persisted object in non-persisted channel");
				}

				instanceFlags |= (byte)ImpunityInstanceFlags.Persisted;
			}

			return new ObjectCreateData(entityTypeId, instanceFlags, propertyBytes, distObj.Name);
		}

		/// <summary>Creates a distributed channel with optional initial child objects.</summary>
		public void CreateChannel<T>(string channelName, T channel, bool replace, IEnumerable<IDistributedEntity> channelObjects, ImpunityCallback<bool> onComplete) where T : class, IDistributedChannel
		{
			channel.Name = channelName;

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
			ArraySegment<byte> propertyBytes = GetPropertyBytes(channel);
			byte instanceFlags = 0;

			if (channel.IsClientAuthoritative)
			{
				instanceFlags |= (byte)ImpunityInstanceFlags.ClientAuthoritative;

				if (DistributedTypes[entityTypeId].Persisted)
				{
					throw new Exception("Can't create a client authoritative channel that is also persisted");
				}
			}
			if (channel.IsPersisted)
			{
				if (!DistributedTypes[entityTypeId].Persisted)
				{
					throw new Exception("Can't create persisted channel of a type that is not persistant");
				}

				instanceFlags |= (byte)ImpunityInstanceFlags.Persisted;
			}

			List<ObjectCreateData> objectCreateList = null;
			if (channelObjects != null)
			{
				objectCreateList = new List<ObjectCreateData>();

				foreach(IDistributedEntity distObj in channelObjects)
				{
					objectCreateList.Add(MakeObjectCreateData(distObj, channel));
				}
			}

			Connection.CreateChannel(channelName, entityTypeId, instanceFlags, propertyBytes, replace, objectCreateList, onComplete);
		}

		/// <summary>Subscribes to a channel by name. If already subscribed, returns the existing channel. Pass a non-null <paramref name="createIfNeeded"/> to create the channel if it doesn't exist.</summary>
		public void SubscribeToChannel<T>(string channelName, T createIfNeeded, ImpunityCallback<T> onComplete) where T : class, IDistributedChannel
		{
			if (channelName == null)
			{
				throw new Exception("Channel must have name");
			}
			if (channelName.Contains("/"))
			{
				throw new Exception("Channel name cannot contain forward slash");
			}

			if (SubscribedChannels.ContainsKey(channelName))
            {
				onComplete(null, (T)SubscribedChannels[channelName]);
				return;
            }

			bool createIfMising = false;
			int entityTypeId = 0;
			byte instaceFlags = 0;
			ArraySegment<byte> propertyBytes = null;

			List<ObjectCreateData> channelObjects = null;
			
			if (createIfNeeded != null)
			{
				createIfMising = true;

				createIfNeeded.Name = channelName;

				Type entityType = createIfNeeded.GetType();
				DistributedEntity distAttr = (DistributedEntity)entityType.GetCustomAttribute(typeof(DistributedEntity));
				if (distAttr == null)
				{
					throw new Exception("Tried to create distributed channel type " + entityType.Name + " with no DistributedEntity attribute");
				}

				entityTypeId = distAttr.EntityId;
				if (entityTypeId <= 0 || entityTypeId >= DistributedTypes.Length || DistributedTypes[entityTypeId] == null)
				{
					throw new Exception("Tried to create distributed channel with invalid entity type id: " + entityTypeId);
				}

				createIfNeeded.DistributedEntityType = entityTypeId;
				propertyBytes = GetPropertyBytes(createIfNeeded);

				if (createIfNeeded.IsClientAuthoritative)
				{
					instaceFlags |= (byte)ImpunityInstanceFlags.ClientAuthoritative;

					if (DistributedTypes[entityTypeId].Persisted)
					{
						throw new Exception("Can't create a client authoritative channel that is also persisted");
					}
				}
				if (createIfNeeded.IsPersisted)
				{
					if (!DistributedTypes[entityTypeId].Persisted)
					{
						throw new Exception("Can't create persisted channel of a type that is not persistant");
					}

					instaceFlags |= (byte)ImpunityInstanceFlags.Persisted;
				}
			}

			Connection.SubcribeToChannel(channelName, createIfMising, entityTypeId, instaceFlags, propertyBytes, channelObjects, (ImpunityErrorResponse err, uint channelId) =>
			{
				if (err != null)
				{
					onComplete?.Invoke(err, null);
					return;
				}

				IDistributedChannel newChannel = (IDistributedChannel)DistributedObjects.GetValueOrDefault(channelId);
				if (newChannel == null)
                {
					// We didn't get the channel create before the action returned, shouldn't happen
					throw new Exception("Didn't get channel create message for subscribed channel " + channelName + " id: " + channelId);
                }

				onComplete?.Invoke(null, (T)newChannel);
			});
        }

		/// <summary>Unsubscribes from a channel and removes it from the local subscription list.</summary>
		public void UnsubscribeFromChannel(IDistributedChannel channel, ImpunityCallback onComplete)
		{
			Connection.UnsubscribeFromChannel(channel.DistributedEntityId, (ImpunityErrorResponse err) =>
			{
				if (err != null)
                {
					onComplete?.Invoke(err);
					return;
                }

				SubscribedChannels.Remove(channel.Name);

				onComplete?.Invoke(null);
			});
		}

		// ---------------

		private GameStateEntityTypeDef RegisterEntityType(Type entityType, List<DistributedTypeInfo> internalTypeInfoList)
        {
			if (!entityType.IsClass)
            {
				throw new Exception("Tried to register distributed entity " + entityType.Name + " that's not a class type");
			}

			GameStateEntityTypeDef entityData = new GameStateEntityTypeDef();
			entityData.Name = entityType.Name;

			DistributedEntity distAttr = (DistributedEntity)entityType.GetCustomAttribute(typeof(DistributedEntity));
			if (distAttr == null)
			{
				throw new Exception("Tried to register distributed entity " + entityType.Name + " with no DistributedEntity attribute");
			}

			entityData.Index = distAttr.EntityId;
			if(entityData.Index <= 0)
            {
				throw new Exception("Entity ID must be positive indeger");
            }

			entityData.PersistedAs = distAttr.PersistAs?.Trim();
			if (entityData.PersistedAs != null)
			{
				if (entityData.PersistedAs.Length == 0)
				{
					throw new Exception("Can't use empty string as PersistedAs value");
				}
				else if (entityData.PersistedAs.StartsWith("_"))
				{
					throw new Exception("Can't start PersistedAs with underscore");
				}
			}

			DistributedTypeInfo internalTypeInfo = new DistributedTypeInfo();
			internalTypeInfo.DistributedTypeId = entityData.Index;
			internalTypeInfo.ObjectType = entityType;
			internalTypeInfo.Persisted = entityData.PersistedAs != null;
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

			bool hasPersistedField = false;

			IEnumerable<FieldInfo> distributedFieldInfos = GetDistributedFields(entityType);
			foreach (var fieldInfo in distributedFieldInfos)
			{

				Distributed fieldAttr = (Distributed)fieldInfo.GetCustomAttribute(typeof(Distributed));

				if (fieldAttr.FieldId <= 0 || fieldAttr.FieldId >= 64)
				{
					throw new Exception("Field ID must be positive integer under 64");
				}

				Type fieldType = fieldInfo.FieldType;
				if (fieldType.GetInterface(nameof(IDistributedField)) == null)
				{
					throw new Exception("Distributed fields must implement IDistributedField");
				}

				bool isTemporalValue = fieldType.GetInterface(nameof(IDistributedTemporalField)) != null;


				// Create a throw-away instance so we can get its type info
				IDistributedField tempFieldValue = (IDistributedField)Activator.CreateInstance(fieldType);

				DistributedTypeFieldInfo dfield = new DistributedTypeFieldInfo();
				dfield.FieldId = fieldAttr.FieldId;
				dfield.FieldName = fieldInfo.Name;
				dfield.FieldType = tempFieldValue.FieldType;
				dfield.FieldValueType = tempFieldValue.ValueType;
				dfield.PersistedAs = fieldAttr.PersistAs?.Trim();
				dfield.IsTemporal = isTemporalValue;

				if (dfield.PersistedAs != null)
				{
					if (dfield.PersistedAs.Length == 0)
					{
						throw new Exception("Can't use empty string as PersistedAs value");
					}
					else if (dfield.PersistedAs.StartsWith("_"))
					{
						throw new Exception("Can't start PersistedAs with underscore");
					}

					if (entityData.PersistedAs == null)
					{
						throw new Exception("Can't have a distributed field persisted if the entity is not persisted");
					}

					hasPersistedField = true;
				}


				MethodInfo writeMethod = GetTypeMethodInherited(entityType, "_imp_WriteChangesWrapper_"+ fieldInfo.Name, BindingFlags.Instance | BindingFlags.NonPublic);
				if (writeMethod == null)
				{
					throw new Exception("Cant find write method for property " + fieldInfo.Name + " on type " + entityType.Name);
				}
				dfield.WriteMethod = writeMethod;

				MethodInfo initMethod = GetTypeMethodInherited(entityType, "_imp_ReadInitialWrapper_" + fieldInfo.Name, BindingFlags.Instance | BindingFlags.NonPublic);
				if (initMethod == null)
				{
					throw new Exception("Cant find init method for property " + fieldInfo.Name + " on type " + entityType.Name);
				}
				dfield.InitMethod = initMethod;

				MethodInfo updateMethod = GetTypeMethodInherited(entityType, "_imp_ReadChangeWrapper_" + fieldInfo.Name, BindingFlags.Instance | BindingFlags.NonPublic);
				if (updateMethod == null)
				{
					throw new Exception("Cant find update method for property " + fieldInfo.Name + " on type " + entityType.Name);
				}
				dfield.UpdateMethod = updateMethod;


				distributedFields.Add(dfield);
			}

			if(distributedFields.Count == 0)
            {
				internalTypeInfoList.Add(internalTypeInfo);
				return entityData;
			}

			if (!hasPersistedField && entityData.PersistedAs != null)
			{
				throw new Exception("Persisted entity has no persisted fields, will store no data");
			}

			entityData.Properties = new GameStateEntityPropertyDef[distributedFields.Count];

			int p = 0;
			foreach (DistributedTypeFieldInfo dfield in distributedFields)
            {
				GameStateEntityPropertyDef propDef = new GameStateEntityPropertyDef();
				propDef.Index = dfield.FieldId;
				propDef.Name = dfield.FieldName;
				propDef.FieldType = (byte)dfield.FieldType;
				propDef.PropValueType = (byte)dfield.FieldValueType;
				propDef.PersistedAs = dfield.PersistedAs;
				propDef.IsTemporal = dfield.IsTemporal;

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

		private static IEnumerable<FieldInfo> GetDistributedFields(Type typeInfo)
		{
			List<FieldInfo> fields = new List<FieldInfo>();

			foreach (var fieldInfo in typeInfo.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
			{

				Distributed fieldAttr = (Distributed)fieldInfo.GetCustomAttribute(typeof(Distributed));
				if (fieldAttr == null)
				{
					continue;
				}

				fields.Add(fieldInfo);
			}

			return fields;
		}

		private static MethodInfo GetTypeMethodInherited(Type typeInfo, string methodName, BindingFlags flags)
		{
			MethodInfo methodInfo = typeInfo.GetMethod(methodName, flags);
			if (methodInfo != null)
			{
				return methodInfo;
			}
			else if (typeInfo.BaseType != typeof(object))
			{
				return GetTypeMethodInherited(typeInfo.BaseType, methodName, flags);
			}

			return null;
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


		public void HandleCreateChannel(uint channelId, string channelName, int channelType, bool isLocked, byte instanceFlags, ArraySegment<byte> propData)
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

			channel.DistributedEntityType = channelType;
			channel.Name = channelName;
			channel.IsLocked = isLocked;
			channel.IsPersisted = ((ImpunityInstanceFlags)instanceFlags & ImpunityInstanceFlags.Persisted) != 0;
			RegisterEntity(channel, channelId);

			SetPropertyBytes(channel, propData, true);

			try
			{
				channel.OnFullyInitialized();
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Excpetion in OnFullyInitialized:", e);
			}			
		}

		public void HandleCreateObject(uint objectId, uint channelId, int objectType, bool isLocked, byte instanceFlags, ArraySegment<byte> propData, string uniqueName, bool newlyCreated)
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

			entity.DistributedEntityType = objectType;
			entity.Name = uniqueName;
			entity.IsLocked = isLocked;
			entity.IsPersisted = ((ImpunityInstanceFlags)instanceFlags & ImpunityInstanceFlags.Persisted) != 0;

			RegisterEntity(entity, objectId);

			IDistributedChannel channel = DistributedObjects[channelId] as IDistributedChannel;

			if (channel == null)
			{
				throw new Exception("No channel with id " + channelId);
			}

			try
			{
				OnDistributedObjectCreated?.Invoke(entity, channel, newlyCreated);
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Excpetion in OnDistributedObjectCreated handler:", e);
			}

			try
			{
				channel.OnObjectAdded(entity, newlyCreated);
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Excpetion in channel OnObjectAdded:", e);
			}

			SetPropertyBytes(entity, propData, true);

			try
			{
				entity.OnFullyInitialized();
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Excpetion in OnFullyInitialized:", e);
			}			
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

			if (entity is IDistributedChannel channel)
			{
				SubscribedChannels.Add(channel.Name, channel);
			}
		}

		private void UnregisterEntity(IDistributedEntity entity)
		{
			DistributedObjects.Remove(entity.DistributedEntityId);

			if (entity is IDistributedChannel channel)
			{
				SubscribedChannels.Remove(channel.Name);
			}
		}

		public void HandleEntityUpdate(uint entityId, ArraySegment<byte> updateData)
		{
			IDistributedEntity entity = DistributedObjects.GetValueOrDefault(entityId);
			if (entity == null)
            {
				ImpunityLogger.LogWarning("Got property update for entity we don't know about: " + entityId);
				return;
            }

			SetPropertyBytes(entity, updateData, false);
		}

		public void HandleEntityEvent(uint entityId, int eventType, BsonValue eventData)
		{
			IDistributedEntity entity = DistributedObjects.GetValueOrDefault(entityId);
			if (entity == null)
			{
				ImpunityLogger.LogWarning("Got event trigger for entity we don't know about: " + entityId);
				return;
			}

			try
			{
				entity.OnEventTriggered(eventType, eventData);
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception in OnEventTriggered: ", e);
			}
		}

		public void HandleEntityLocked(uint entityId)
		{
			IDistributedEntity entity = DistributedObjects.GetValueOrDefault(entityId);
			if (entity == null)
			{
				ImpunityLogger.LogWarning("Got lock for entity we don't know about: " + entityId);
				return;
			}

			entity.IsLocked = true;
			entity.OnLocked();
		}

		public void HandleEntityUnlocked(uint entityId)
		{
			IDistributedEntity entity = DistributedObjects.GetValueOrDefault(entityId);
			if (entity == null)
			{
				ImpunityLogger.LogWarning("Got unlock for entity we don't know about: " + entityId);
				return;
			}

			entity.IsLocked = false;
			entity.OnUnlocked();
		}

		public void HandleEntityDelete(uint entityId, BsonValue deleteData)
		{
			IDistributedEntity entity = DistributedObjects.GetValueOrDefault(entityId);
			if (entity == null)
			{
				ImpunityLogger.LogWarning("Got delete for entity we don't know about: " + entityId);
				return;
			}

			if (entity is IDistributedChannel channel)
			{
				foreach(var obj in channel.DistributedObjects.Values)
				{
					UnregisterEntity(obj);

					try
					{
						obj.OnUndistributed();
					}
					catch (Exception e)
					{
						ImpunityLogger.LogError("Exception in OnUndistributed: ", e);
					}
				}
			}

			UnregisterEntity(entity);
			try
			{
				entity.OnDeleted(deleteData);
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception in OnDeleted: ", e);
			}
		}

		/// <summary>Marks an entity as having dirty properties that need to be sent to the server on the next <see cref="SendUpdates"/> call.</summary>
		public void SetDirty(IDistributedEntity entity)
        {
			DirtyObjects.Add(entity);

		}

		/// <summary>Serializes and sends all dirty entity property changes to the server. Called each frame by <see cref="BaseGameConnection.Update"/>.</summary>
		public void SendUpdates()
        {
			PropertyEncodingWriter.BaseStream.Position = 0;
			
			foreach (IDistributedEntity entity in DirtyObjects)
            {
				SendEntityUpdates(entity);
			}

			DirtyObjects.Clear();

		}

		public ArraySegment<byte> GetPropertyBytes(IDistributedEntity entity, bool allProperties = false)
        {
			DistributedTypeInfo typeInfo = DistributedTypes[entity.DistributedEntityType];

			ulong dirtyBits = allProperties ? ulong.MaxValue : entity.DirtyBits;
			if (dirtyBits == 0)
            {
				return null;
			}

			int startPos = (int)PropertyEncodingWriter.BaseStream.Position;

			foreach (var fieldInfo in typeInfo.DistributedFields)
			{
				if (fieldInfo == null) continue;

				if ((dirtyBits & (1ul << (fieldInfo.FieldId - 1))) != 0)
				{
					PropertyEncodingWriter.Write((byte)fieldInfo.FieldId);
					fieldInfo.WriteMethod.Invoke(entity, WriteMethodArgs);
				}
			}

			PropertyEncodingWriter.Write((byte)0);

			entity.ClearDirty();
			int bufferSize = (int)PropertyEncodingWriter.BaseStream.Position - startPos;

			return new ArraySegment<byte>(PropertyEncodingBuffer, startPos, bufferSize);
		}

		public void SetPropertyBytes(IDistributedEntity entity, ArraySegment<byte> propertyBytes, bool initialRead)
        {
			if (propertyBytes == null || propertyBytes.Count == 0)
            {
				return;
            }

			DistributedTypeInfo typeInfo = DistributedTypes[entity.DistributedEntityType];
			DistributedTypeFieldInfo[] fields = typeInfo.DistributedFields;

			PropertyDecodingReader.BaseStream.Position = 0;
			PropertyDecodingReader.BaseStream.Write(propertyBytes);
			PropertyDecodingReader.BaseStream.Position = 0;

			while (true)
			{
				int propId = PropertyDecodingReader.ReadByte();
				if (propId == 0)
				{
					break;
				}

				if (propId <= 0 || propId >= fields.Length)
				{
					throw new Exception("Invalid property id: " + propId);
				}

				DistributedTypeFieldInfo fieldInfo = fields[propId];
				if (fieldInfo == null)
                {
					throw new Exception("Invalid property id: " + propId);
				}

				if (initialRead)
				{
					fieldInfo.InitMethod.Invoke(entity, UpdateMethodArgs);
				}
				else
				{
					fieldInfo.UpdateMethod.Invoke(entity, UpdateMethodArgs);
				}

			}
		}

		private void SendEntityUpdates(IDistributedEntity entity)
        {
			ArraySegment<byte> updateDatabuffer = GetPropertyBytes(entity);

			Connection.UpdateEntity(entity.DistributedEntityId, updateDatabuffer, null);

		}
	}

}