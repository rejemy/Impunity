using System;
using System.Collections.Concurrent;
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
		public string? FactoryMethod { get; set; }
		public string? PersistAs { get; set; }

		public DistributedEntity(int entityId)
		{
			EntityId = entityId;
		}

		private static readonly ConcurrentDictionary<Type, int> EntityTypeIdCache = new ConcurrentDictionary<Type, int>();

		/// <summary>Returns the entity type id declared by the <see cref="DistributedEntity"/> attribute on
		/// <paramref name="type"/>, or 0 if it has none. Cached per type; used by the entity base constructors
		/// so a freshly-constructed instance knows its type id without going through the manager.</summary>
		internal static int GetEntityTypeId(Type type)
		{
			return EntityTypeIdCache.GetOrAdd(type, static t =>
				((DistributedEntity?)GetCustomAttribute(t, typeof(DistributedEntity)))?.EntityId ?? 0);
		}
	}

	/// <summary>Marks a field as a distributed property with a unique byte ID (1-63). Optionally specifies a persistence key.</summary>
	[AttributeUsage(AttributeTargets.Field)]
	public class Distributed : Attribute
	{
		internal byte FieldId;
		public string? PersistAs { get; set; }

		public Distributed(byte fieldId)
		{
			FieldId = fieldId;
		}
	}


	/// <summary>Client-side interface for a distributed entity. Provides identity, dirty tracking, lock/event/delete operations, and lifecycle callbacks.</summary>
	public interface IDistributedEntity
	{
		uint DistributedEntityId { get; set; }
		int DistributedEntityType { get; set; }
		bool IsClientAuthoritative { get; set; }
		bool IsPersisted { get; set; }
		bool IsLocked { get; set; }

		ClientEntityManager Manager { get; set; }

		ulong DirtyBits { get; }
		bool DirtyGuaranteed { get; }

		/// <summary>Per-entity outgoing sequence counter, incremented each time this entity sends an update.</summary>
		ushort SendSeq { get; set; }
		/// <summary>Per-field last-received sequence numbers, indexed by field ID. Used to detect and discard stale out-of-order updates.</summary>
		ushort[]? FieldRecvSeq { get; set; }

		void SetDirty(ulong fieldBitmask, bool guaranteed);

		void ClearDirty();

		void InitializeDistributedFields();

		void TriggerEvent(int eventType, BsonValue eventData, ImpunityCallback onComplete);
		void Delete(BsonValue deleteData, ImpunityCallback<bool> onComplete);
		void TryLock(ImpunityCallback<bool> onComplete);
		void WaitForLock(ImpunityCallback<LockWaitResult> onComplete);
		void Unlock(ImpunityCallback<bool> onComplete);

		void OnFullyInitialized();
		void OnEventTriggered(int eventType, BsonValue eventData);
		void OnDeleted(BsonValue? deleteData);
		void OnLocked();
		void OnUnlocked();
		void OnUndistributed();
	}

	/// <summary>Client-side interface for a distributed object entity.</summary>
	public interface IDistributedObject : IDistributedEntity
	{
		string? UniqueName { get; set; }
	}

	/// <summary>Client-side interface for a distributed channel entity. Channels contain child objects and support subscription.</summary>
	public interface IDistributedChannel : IDistributedEntity
	{
		string Name { get; set; }
		Dictionary<uint, IDistributedObject> DistributedObjects { get; }

		void Unsubscribe(ImpunityCallback onComplete, bool immediate = false);

		void OnObjectAdded(IDistributedObject entity, bool newlyCreated);
		void OnObjectRemoved(uint entityId, bool destroyed);
	}

	/// <summary>Base implementation of <see cref="IDistributedEntity"/> with dirty-bit tracking, lock waiting, and lifecycle callback stubs.</summary>
	public abstract class DistributedEntityBase : IDistributedEntity
	{
		public uint DistributedEntityId { get; set; }
		public int DistributedEntityType { get; set; }
		public bool IsClientAuthoritative { get; set; }
		public bool IsPersisted { get; set; }
		public bool IsLocked { get; set; }
		private event ImpunityCallback<LockWaitResult>? LockWaiter;

		public ClientEntityManager Manager { get; set; } = default!;

		public ulong DirtyBits { get; private set; }
		public bool DirtyGuaranteed { get; private set; }

		public ushort SendSeq { get; set; }
		public ushort[]? FieldRecvSeq { get; set; }

		protected DistributedEntityBase()
		{
			// The type id is intrinsic to the [DistributedEntity] attribute, so populate it at
			// construction. This makes offline/editor-built instances (which never go through
			// CreateObject/HandleCreate*) usable with the manager's persisted-field BSON methods.
			DistributedEntityType = DistributedEntity.GetEntityTypeId(GetType());
			InitializeDistributedFields();
		}

		public void SetDirty(ulong fieldBitmask, bool guaranteed)
        {
			DirtyBits |= fieldBitmask;
			DirtyGuaranteed |= guaranteed;
			Manager?.SetDirty(this);
		}

		public void ClearDirty()
        {
			DirtyBits = 0ul;
			DirtyGuaranteed = false;
		}

		public virtual void InitializeDistributedFields() {}

		public void TriggerEvent(int eventType, BsonValue eventData, ImpunityCallback onComplete)
		{
			Manager.Connection?.TriggerEntityEvent(DistributedEntityId, eventType, eventData, onComplete);
		}

		public void Delete(BsonValue deleteData, ImpunityCallback<bool> onComplete)
		{
			Manager.Connection?.DeleteEntity(DistributedEntityId, deleteData, onComplete);
		}

		public void TryLock(ImpunityCallback<bool> onComplete)
		{
			Manager.Connection?.TryToLockEntity(DistributedEntityId, onComplete);
		}

		public void WaitForLock(ImpunityCallback<LockWaitResult> onComplete)
		{
			Manager.Connection?.TryToLockEntity(DistributedEntityId, (err, lockResult) =>
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
			Manager.Connection?.UnlockEntity(DistributedEntityId, onComplete);
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
		public virtual void OnDeleted(BsonValue? deleteData) { }
		public virtual void OnUndistributed() { }
	}

	/// <summary>Base implementation of <see cref="IDistributedObject"/>.</summary>
	public abstract class DistributedObjectBase : DistributedEntityBase, IDistributedObject
	{
		public string? UniqueName { get; set; }
	}

	/// <summary>Base implementation of <see cref="IDistributedChannel"/>. Maintains a dictionary of child objects and supports unsubscription.</summary>
	public abstract class DistributedChannelBase : DistributedEntityBase, IDistributedChannel
	{
		public string Name { get; set; } = default!;

		public Dictionary<uint, IDistributedObject> DistributedObjects { get; private set; } = new Dictionary<uint, IDistributedObject>();

		public void Unsubscribe(ImpunityCallback onComplete, bool immediate = false)
		{
			Manager.UnsubscribeFromChannel(this, onComplete, immediate);
		}

		public virtual void OnObjectAdded(IDistributedObject entity, bool newlyCreated)
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
		public UInt64 FieldBitmask;
		public string FieldName;
		public string? PersistedAs;
		public bool IsTemporal;
		public GameStateEntityFieldType FieldType;
		public GameStateEntityPropertyValueType FieldValueType;
		/// <summary>The CLR value type the field carries (the <c>T</c> of <c>DistributedValue&lt;T,S&gt;</c>; the element/value type for collections).</summary>
		public Type ValueClrType = null!;
		public MethodInfo WriteMethod;
		public MethodInfo InitMethod;
		public MethodInfo UpdateMethod;
		public MethodInfo SkipMethod;
		public MethodInfo GetAsBsonMethod;
		public MethodInfo SetFromBsonMethod;

		public DistributedTypeFieldInfo(byte fieldId, UInt64 fieldBitmask, string fieldName, string? persistedAs, bool isTemporal,
			GameStateEntityFieldType fieldType, GameStateEntityPropertyValueType fieldValueType,
			MethodInfo writeMethod, MethodInfo initMethod, MethodInfo updateMethod, MethodInfo skipMethod, MethodInfo getAsBsonMethod, MethodInfo setFromBsonMethod)
		{
			this.FieldId = fieldId;
			this.FieldBitmask = fieldBitmask;
			this.FieldName = fieldName;
			this.PersistedAs = persistedAs;
			this.IsTemporal = isTemporal;
			this.FieldType = fieldType;
			this.FieldValueType = fieldValueType;
			this.WriteMethod = writeMethod;
			this.InitMethod = initMethod;
			this.UpdateMethod = updateMethod;
			this.SkipMethod = skipMethod;
			this.GetAsBsonMethod = getAsBsonMethod;
			this.SetFromBsonMethod = setFromBsonMethod;
		}
	}

	/// <summary>Internal metadata for a distributed entity type: type ID, factory, field definitions, channel/persistence flags.</summary>
	public class DistributedTypeInfo
	{
		public int DistributedTypeId;
		public bool IsChannel;
		public bool Persisted;
		public Type ObjectType;
		public Func<IDistributedEntity>? Factory;
		public DistributedTypeFieldInfo[] DistributedFields = null!;

		public DistributedTypeInfo(int distributedTypeId, Type objectType, bool persisted)
		{
			DistributedTypeId = distributedTypeId;
			ObjectType = objectType;
			Persisted = persisted;
		}
	}


	/// <summary>
	/// Public read-only description of a single distributed field, for tools (e.g. an editor's property
	/// dialog) that need to classify fields and know their CLR type without touching the internal metadata.
	/// Derive Primitive/Complex/Collection/Temporal from <see cref="FieldType"/> + <see cref="ValueType"/>:
	/// <see cref="FieldType"/> distinguishes scalar vs. array/queue/dictionary, and a <see cref="ValueType"/>
	/// of <c>Custom</c>/<c>CustomSmall</c> (and their nullable variants) denotes a complex value.
	/// </summary>
	public sealed class DistributedFieldInfo
	{
		/// <summary>The field's unique byte id (1-63).</summary>
		public byte FieldId;
		/// <summary>The CLR field name on the entity.</summary>
		public string FieldName = null!;
		/// <summary>The persistence key from <c>[Distributed(PersistAs=…)]</c>, or null if the field is not persisted.</summary>
		public string? PersistAs;
		/// <summary>True for temporal fields (never persisted).</summary>
		public bool IsTemporal;
		/// <summary>The container kind: Value, Array, Queue, IntDictionary, or StringDictionary.</summary>
		public GameStateEntityFieldType FieldType;
		/// <summary>The wire value type, including Custom/CustomSmall (and nullable variants) for complex values.</summary>
		public GameStateEntityPropertyValueType ValueType;
		/// <summary>The CLR value type (T; the element/value type for collections).</summary>
		public Type ValueClrType = null!;
	}


	/// <summary>
	/// Client-side manager for distributed entities. Handles type registration, entity creation/subscription,
	/// dirty tracking, property serialization/deserialization, and dispatching server state updates to entity instances.
	/// </summary>
	public class ClientEntityManager
	{
		/// <summary>The connection this manager sends actions through.</summary>
		public BaseGameConnection? Connection = default;

		private DistributedTypeInfo[] DistributedTypes = default!;

		private Dictionary<string, IDistributedChannel> SubscribedChannels;
		private Dictionary<uint, IDistributedEntity> DistributedObjects;
		private HashSet<IDistributedEntity> DirtyObjects;

		// Suppression registry for in-flight unsubscribes (immediate mode only).
		// SuppressedEntities maps entityId -> owning channelId for O(1) drop checks.
		// PendingUnsubscribes maps channelId -> every suppressed id under it (channel + children
		// + in-flight new objects), so suppression can be lifted in bulk when the ack returns.
		private Dictionary<uint, uint> SuppressedEntities;
		private Dictionary<uint, HashSet<uint>> PendingUnsubscribes;

		private byte[] PropertyEncodingBuffer;
		private BinaryWriter PropertyEncodingWriter;
		private object[] WriteMethodArgs;

		private byte[] PropertyDecodingBuffer;
		private BinaryReader PropertyDecodingReader;
		private object[] UpdateMethodArgs;

		private object[] GetAsBsonMethodArgs = new object[0];

		/// <summary>Called when a distributed object is created in any subscribed channel. Parameters: entity, parent channel, newly created flag.</summary>
		public Action<IDistributedObject, IDistributedChannel, bool>? OnDistributedObjectCreated;

		public ClientEntityManager()
        {
			SubscribedChannels = new Dictionary<string, IDistributedChannel>();
			DistributedObjects = new Dictionary<uint, IDistributedEntity>();
			DirtyObjects = new HashSet<IDistributedEntity>();

			SuppressedEntities = new Dictionary<uint, uint>();
			PendingUnsubscribes = new Dictionary<uint, HashSet<uint>>();

			PropertyEncodingBuffer = new byte[ImpunityConstants.MaxMessageSize];
			PropertyEncodingWriter = new BinaryWriter(new MemoryStream(PropertyEncodingBuffer));
			WriteMethodArgs = new object[] { PropertyEncodingWriter };

			PropertyDecodingBuffer = new byte[ImpunityConstants.MaxMessageSize];
			PropertyDecodingReader = new BinaryReader(new MemoryStream(PropertyDecodingBuffer));
			UpdateMethodArgs = new object[] { PropertyDecodingReader };
		}

		// -------------- Public API

		/// <summary>Registers all distributed entity types via reflection. Builds internal type metadata and returns format definitions for the server handshake.</summary>
		public GameStateEntityTypeDef[]? RegisterEntityTypes(Type[] entityTypes)
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
		public void CreateObject<T>(T distObj, IDistributedChannel channel, bool replace, ImpunityCallback<T> onComplete) where T : class, IDistributedObject
		{
			if (Connection == null)
			{
				throw new Exception("ClientEntityManager has no connection");
			}

			if(distObj.UniqueName != null && distObj.UniqueName.Contains("/"))
			{
				throw new Exception("Object name cannot contain forward slash");
			}

			int entityTypeId = distObj.DistributedEntityType;
	
			ArraySegment<byte> propertyBytes = GetPropertyBytes(distObj, out _);

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

			Connection.CreateObject(entityTypeId, instaceFlags, channel.DistributedEntityId, propertyBytes, distObj.UniqueName, replace, (ImpunityErrorResponse? err, uint objectId) =>
			{
				if (err != null)
				{
					onComplete?.Invoke(err, null!);
					return;
				}

				RegisterEntity(distObj, objectId);
				onComplete?.Invoke(err, distObj);
			});
		}

		private ObjectCreateData MakeObjectCreateData(IDistributedObject distObj, IDistributedChannel channel)
		{
			ObjectCreateData data = new ObjectCreateData();

			if(distObj.UniqueName != null && distObj.UniqueName.Contains("/"))
			{
				throw new Exception("Object name cannot contain forward slash");
			}

			int entityTypeId = distObj.DistributedEntityType;

			ArraySegment<byte> propertyBytes = GetPropertyBytes(distObj, out _);

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

			return new ObjectCreateData(entityTypeId, instanceFlags, propertyBytes, distObj.UniqueName);
		}

		/// <summary>Creates a distributed channel with optional initial child objects.</summary>
		public void CreateChannel<T>(string channelName, T channel, bool replace, IEnumerable<IDistributedObject> channelObjects, ImpunityCallback<bool> onComplete) where T : class, IDistributedChannel
		{
			if (Connection == null)
			{
				throw new Exception("ClientEntityManager has no connection");
			}

			channel.Name = channelName;

			int entityTypeId = channel.DistributedEntityType;

			ArraySegment<byte> propertyBytes = GetPropertyBytes(channel, out _);
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

			List<ObjectCreateData>? objectCreateList = null;
			if (channelObjects != null)
			{
				objectCreateList = new List<ObjectCreateData>();

				foreach(IDistributedObject distObj in channelObjects)
				{
					objectCreateList.Add(MakeObjectCreateData(distObj, channel));
				}
			}

			Connection.CreateChannel(channelName, entityTypeId, instanceFlags, propertyBytes, replace, objectCreateList, onComplete);
		}

		/// <summary>Subscribes to a channel by name. If already subscribed, returns the existing channel. Pass a non-null <paramref name="createIfNeeded"/> to create the channel if it doesn't exist.</summary>
		public void SubscribeToChannel<T>(string channelName, T createIfNeeded, ImpunityCallback<T> onComplete) where T : class, IDistributedChannel
		{
			if (Connection == null)
			{
				throw new Exception("ClientEntityManager has no connection");
			}

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


			if (createIfNeeded != null)
			{
				createIfMising = true;

				createIfNeeded.Name = channelName;

				entityTypeId = createIfNeeded.DistributedEntityType;

				propertyBytes = GetPropertyBytes(createIfNeeded, out _);

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

			Connection.SubcribeToChannel(channelName, createIfMising, entityTypeId, instaceFlags, propertyBytes, null, (ImpunityErrorResponse? err, uint channelId) =>
			{
				if (err != null)
				{
					onComplete?.Invoke(err, null!);
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

		/// <summary>
		/// Unsubscribes from a channel and removes it (and all its objects) from the manager.
		/// When <paramref name="immediate"/> is false (default), the channel and its objects stay
		/// live and continue to receive updates until the server acknowledges the unsubscribe, at
		/// which point <see cref="IDistributedEntity.OnUndistributed"/> is invoked on each and all
		/// references are released. When <paramref name="immediate"/> is true, the channel and its
		/// objects are unregistered synchronously and all further incoming updates for them are
		/// suppressed (no lifecycle callbacks) — the caller is responsible for cleaning them up.
		/// </summary>
		public void UnsubscribeFromChannel(IDistributedChannel channel, ImpunityCallback onComplete, bool immediate = false)
		{
			if (Connection == null)
			{
				throw new Exception("ClientEntityManager has no connection");
			}

			uint channelId = channel.DistributedEntityId;

			if (immediate)
			{
				// Build + register the suppression set BEFORE sending, so any message dispatched
				// afterward (including ones already queued in CompletedActions) is dropped.
				HashSet<uint> set = new HashSet<uint> { channelId };
				SuppressedEntities[channelId] = channelId;
				foreach (IDistributedObject child in channel.DistributedObjects.Values)
				{
					set.Add(child.DistributedEntityId);
					SuppressedEntities[child.DistributedEntityId] = channelId;
				}
				PendingUnsubscribes[channelId] = set;

				TearDownChannel(channel, invokeCallbacks: false);
			}

			Connection.UnsubscribeFromChannel(channelId, (ImpunityErrorResponse? err) =>
			{
				if (immediate)
				{
					// Lift suppression — the server has stopped sending and the ack is ordered
					// after all prior channel messages, so nothing more is in flight.
					if (PendingUnsubscribes.TryGetValue(channelId, out HashSet<uint> s))
					{
						foreach (uint id in s)
						{
							SuppressedEntities.Remove(id);
						}
						PendingUnsubscribes.Remove(channelId);
					}
				}
				else if (err == null)
				{
					// Deferred teardown: objects stayed live during the window; tear down now,
					// invoking lifecycle callbacks so app code can release its resources.
					TearDownChannel(channel, invokeCallbacks: true);
				}

				onComplete?.Invoke(err);
			});
		}

		/// <summary>
		/// Unregisters a channel and all its child objects from the manager. When
		/// <paramref name="invokeCallbacks"/> is true, fires <see cref="IDistributedEntity.OnUndistributed"/>
		/// on each child object and then the channel.
		/// </summary>
		private void TearDownChannel(IDistributedChannel channel, bool invokeCallbacks)
		{
			foreach (IDistributedObject child in new List<IDistributedObject>(channel.DistributedObjects.Values))
			{
				UnregisterEntity(child);

				if (invokeCallbacks)
				{
					try
					{
						child.OnUndistributed();
					}
					catch (Exception e)
					{
						ImpunityLogger.LogError("Exception in OnUndistributed: ", e);
					}
				}
			}

			channel.DistributedObjects.Clear();

			UnregisterEntity(channel);

			if (invokeCallbacks)
			{
				try
				{
					channel.OnUndistributed();
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception in OnUndistributed: ", e);
				}
			}
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

			DistributedTypeInfo internalTypeInfo = new DistributedTypeInfo(entityData.Index, entityType, entityData.PersistedAs != null);

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

				var fieldBitmask = 1ul << (fieldAttr.FieldId - 1);
				var fieldPersistedAs = fieldAttr.PersistAs?.Trim();

				if (fieldPersistedAs != null)
				{
					if (fieldPersistedAs.Length == 0)
					{
						throw new Exception("Can't use empty string as PersistedAs value");
					}
					else if (fieldPersistedAs.StartsWith("_"))
					{
						throw new Exception("Can't start PersistedAs with underscore");
					}

					if (entityData.PersistedAs == null)
					{
						throw new Exception("Can't have a distributed field persisted if the entity is not persisted");
					}

					if (isTemporalValue)
					{
						throw new Exception("A temporal field can't be persisted");
					}

					hasPersistedField = true;
				}

				MethodInfo? writeMethod = GetTypeMethodInherited(entityType, "_imp_WriteChangesWrapper_"+ fieldInfo.Name, BindingFlags.Instance | BindingFlags.NonPublic);
				if (writeMethod == null)
				{
					throw new Exception("Cant find write method for property " + fieldInfo.Name + " on type " + entityType.Name);
				}

				MethodInfo? initMethod = GetTypeMethodInherited(entityType, "_imp_ReadInitialWrapper_" + fieldInfo.Name, BindingFlags.Instance | BindingFlags.NonPublic);
				if (initMethod == null)
				{
					throw new Exception("Cant find init method for property " + fieldInfo.Name + " on type " + entityType.Name);
				}

				MethodInfo? updateMethod = GetTypeMethodInherited(entityType, "_imp_ReadChangeWrapper_" + fieldInfo.Name, BindingFlags.Instance | BindingFlags.NonPublic);
				if (updateMethod == null)
				{
					throw new Exception("Cant find update method for property " + fieldInfo.Name + " on type " + entityType.Name);
				}

				MethodInfo? skipMethod = GetTypeMethodInherited(entityType, "_imp_SkipWrapper_" + fieldInfo.Name, BindingFlags.Instance | BindingFlags.NonPublic);
				if (skipMethod == null)
				{
					throw new Exception("Cant find skip method for property " + fieldInfo.Name + " on type " + entityType.Name);
				}

				MethodInfo? getAsBsonMethod = GetTypeMethodInherited(entityType, "_imp_GetBsonValueWrapper_" + fieldInfo.Name, BindingFlags.Instance | BindingFlags.NonPublic);
				if (getAsBsonMethod == null)
				{
					throw new Exception("Cant find getAsBson method for property " + fieldInfo.Name + " on type " + entityType.Name);
				}

				MethodInfo? setFromBsonMethod = GetTypeMethodInherited(entityType, "_imp_SetFromBsonValueWrapper_" + fieldInfo.Name, BindingFlags.Instance | BindingFlags.NonPublic);
				if (setFromBsonMethod == null)
				{
					throw new Exception("Cant find setFromBson method for property " + fieldInfo.Name + " on type " + entityType.Name);
				}


				DistributedTypeFieldInfo dfield = new DistributedTypeFieldInfo(fieldAttr.FieldId, fieldBitmask, fieldInfo.Name, fieldAttr.PersistAs?.Trim(),
																				isTemporalValue, tempFieldValue.FieldType, tempFieldValue.ValueType,
																				writeMethod, initMethod, updateMethod, skipMethod, getAsBsonMethod, setFromBsonMethod);

				// The field's first generic argument is the value CLR type (T), i.e. the element type
				// for arrays/queues and the value type for dictionaries.
				dfield.ValueClrType = fieldType.GetGenericArguments()[0];

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
				GameStateEntityPropertyDef propDef = new GameStateEntityPropertyDef(
					dfield.FieldId,
					dfield.FieldName,
					(byte)dfield.FieldType,
					(byte)dfield.FieldValueType,
					dfield.PersistedAs,
					dfield.IsTemporal
				);

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

		private static MethodInfo? GetTypeMethodInherited(Type typeInfo, string methodName, BindingFlags flags)
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
			// A fresh channel-create means the client has re-subscribed to this id; clear any
			// lingering suppression so the re-established channel and its objects flow normally.
			if (PendingUnsubscribes.TryGetValue(channelId, out HashSet<uint> suppressed))
			{
				foreach (uint id in suppressed)
				{
					SuppressedEntities.Remove(id);
				}
				PendingUnsubscribes.Remove(channelId);
			}

			IDistributedChannel channel;
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

		public void HandleCreateObject(uint objectId, uint channelId, int objectType, bool isLocked, byte instanceFlags, ArraySegment<byte> propData, string? uniqueName, bool newlyCreated)
		{
			// In-flight create on a channel being immediately-unsubscribed. Drop it, and also
			// suppress this brand-new object id so its later updates drop quietly rather than
			// logging "unknown entity" warnings.
			if (SuppressedEntities.ContainsKey(channelId))
			{
				if (PendingUnsubscribes.TryGetValue(channelId, out HashSet<uint> suppressed))
				{
					suppressed.Add(objectId);
				}
				SuppressedEntities[objectId] = channelId;
				return;
			}

			IDistributedObject entity;

			DistributedTypeInfo typeInfo = DistributedTypes[objectType];
			if (typeInfo.Factory != null)
			{
				entity = (IDistributedObject)typeInfo.Factory();
			}
			else
			{
				entity = (IDistributedObject)Activator.CreateInstance(typeInfo.ObjectType);
			}

			entity.DistributedEntityType = objectType;
			entity.UniqueName = uniqueName;
			entity.IsLocked = isLocked;
			entity.IsPersisted = ((ImpunityInstanceFlags)instanceFlags & ImpunityInstanceFlags.Persisted) != 0;

			RegisterEntity(entity, objectId);

			IDistributedChannel? channel = DistributedObjects[channelId] as IDistributedChannel;

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

			DistributedTypeInfo typeInfo = DistributedTypes[entity.DistributedEntityType];
			if (typeInfo.DistributedFields != null)
			{
				entity.FieldRecvSeq = new ushort[typeInfo.DistributedFields.Length];
			}

			DistributedObjects[entity.DistributedEntityId] = entity;

			if (entity.DirtyBits != 0)
			{
				SetDirty(entity);
			}

			if (entity is IDistributedChannel channel)
			{
				SubscribedChannels.Add(channel.Name!, channel);
			}
		}

		private void UnregisterEntity(IDistributedEntity entity)
		{
			DistributedObjects.Remove(entity.DistributedEntityId);

			if (entity is IDistributedChannel channel)
			{
				SubscribedChannels.Remove(channel.Name!);
			}
		}

			public void HandleEntityUpdate(uint entityId, ArraySegment<byte> updateData, ushort seq)
		{
			if (SuppressedEntities.ContainsKey(entityId)) return;

			IDistributedEntity entity = DistributedObjects.GetValueOrDefault(entityId);
			if (entity == null)
            {
				ImpunityLogger.LogWarning("Got property update for entity we don't know about: " + entityId);
				return;
            }

			SetPropertyBytes(entity, updateData, false, seq);
		}

		public void HandleEntityEvent(uint entityId, int eventType, BsonValue eventData)
		{
			if (SuppressedEntities.ContainsKey(entityId)) return;

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
			if (SuppressedEntities.ContainsKey(entityId)) return;

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
			if (SuppressedEntities.ContainsKey(entityId)) return;

			IDistributedEntity entity = DistributedObjects.GetValueOrDefault(entityId);
			if (entity == null)
			{
				ImpunityLogger.LogWarning("Got unlock for entity we don't know about: " + entityId);
				return;
			}

			entity.IsLocked = false;
			entity.OnUnlocked();
		}

		public void HandleEntityDelete(uint entityId, BsonValue? deleteData)
		{
			if (SuppressedEntities.ContainsKey(entityId)) return;

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

		public ArraySegment<byte> GetPropertyBytes(IDistributedEntity entity, out bool guaranteed, bool allProperties = false)
        {
			DistributedTypeInfo typeInfo = DistributedTypes[entity.DistributedEntityType];

			guaranteed = entity.DirtyGuaranteed;

			ulong dirtyBits = allProperties ? ulong.MaxValue : entity.DirtyBits;
			if (dirtyBits == 0)
            {
				return null;
			}

			int startPos = (int)PropertyEncodingWriter.BaseStream.Position;

			foreach (var fieldInfo in typeInfo.DistributedFields)
			{
				if (fieldInfo == null) continue;

				if ((dirtyBits & fieldInfo.FieldBitmask) != 0)
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

		public void SetPropertyBytes(IDistributedEntity entity, ArraySegment<byte> propertyBytes, bool initialRead, ushort seq = 0)
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
				else if (seq == 0 || entity.FieldRecvSeq == null || (short)(seq - entity.FieldRecvSeq[propId]) > 0)
				{
					fieldInfo.UpdateMethod.Invoke(entity, UpdateMethodArgs);
					if (entity.FieldRecvSeq != null)
					{
						entity.FieldRecvSeq[propId] = seq;
					}
				}
				else
				{
					fieldInfo.SkipMethod.Invoke(entity, UpdateMethodArgs);
				}

			}
		}

		private void SendEntityUpdates(IDistributedEntity entity)
        {
			if (Connection == null)
			{
				return;
			}

			ArraySegment<byte> updateDatabuffer = GetPropertyBytes(entity, out bool guaranteedSend);

			entity.SendSeq++;
			Connection.UpdateEntity(entity.DistributedEntityId, updateDatabuffer, guaranteedSend, entity.SendSeq, null);

		}

		/// <summary>Resolves the registered type metadata for an entity by its <see cref="IDistributedEntity.DistributedEntityType"/>,
		/// throwing a clear error rather than indexing the reserved/empty slots when the type isn't registered with this manager
		/// (e.g. a bare instance whose type id was never set, or one registered with a different manager).</summary>
		private DistributedTypeInfo GetRegisteredTypeInfo(IDistributedEntity entity)
		{
			int typeId = entity.DistributedEntityType;
			if (typeId <= 0 || typeId >= DistributedTypes.Length || DistributedTypes[typeId] == null)
			{
				throw new Exception("Entity type " + entity.GetType().Name + " (id " + typeId + ") is not registered with this manager");
			}

			return DistributedTypes[typeId];
		}

		public BsonDocument GetPersistedFieldsAsBson(IDistributedEntity entity)
		{
			DistributedTypeInfo typeInfo = GetRegisteredTypeInfo(entity);
			BsonDocument persistedDoc = new BsonDocument();

			foreach (var fieldInfo in typeInfo.DistributedFields)
			{
				if (fieldInfo == null) continue;

				if (fieldInfo.PersistedAs == null) continue;

				persistedDoc[fieldInfo.PersistedAs] = (BsonValue)fieldInfo.GetAsBsonMethod.Invoke(entity, GetAsBsonMethodArgs);
			}

			return persistedDoc;
		}

		public void ApplyPersistedFieldsFromBson(IDistributedEntity entity, BsonDocument doc)
		{
			DistributedTypeInfo typeInfo = GetRegisteredTypeInfo(entity);

			foreach (var fieldInfo in typeInfo.DistributedFields)
			{
				if (fieldInfo == null) continue;

				if (fieldInfo.PersistedAs == null) continue;
				BsonValue fieldValue = doc[fieldInfo.PersistedAs];
				if(fieldValue == null || fieldValue.IsNull) continue;

				object[] parameters = new object[] { fieldValue };
				fieldInfo.SetFromBsonMethod.Invoke(entity, parameters);
			}
		}

		/// <summary>
		/// Returns a read-only description of every distributed field on a registered entity type, for tools
		/// that need to enumerate and classify fields (id, name, persistence key, container kind, value type,
		/// and CLR value type). Throws if <paramref name="entityType"/> is not registered with this manager.
		/// </summary>
		public IReadOnlyList<DistributedFieldInfo> GetFieldSchema(Type entityType)
		{
			int typeId = DistributedEntity.GetEntityTypeId(entityType);
			if (typeId <= 0 || typeId >= DistributedTypes.Length || DistributedTypes[typeId] == null)
			{
				throw new Exception("Entity type " + entityType.Name + " is not registered with this manager");
			}

			DistributedTypeInfo typeInfo = DistributedTypes[typeId];

			List<DistributedFieldInfo> schema = new List<DistributedFieldInfo>();
			if (typeInfo.DistributedFields == null)
			{
				return schema;
			}

			foreach (DistributedTypeFieldInfo field in typeInfo.DistributedFields)
			{
				if (field == null) continue;

				schema.Add(new DistributedFieldInfo
				{
					FieldId = field.FieldId,
					FieldName = field.FieldName,
					PersistAs = field.PersistedAs,
					IsTemporal = field.IsTemporal,
					FieldType = field.FieldType,
					ValueType = field.FieldValueType,
					ValueClrType = field.ValueClrType,
				});
			}

			return schema;
		}
	}

}