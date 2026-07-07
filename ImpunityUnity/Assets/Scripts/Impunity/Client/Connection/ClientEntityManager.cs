using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Impunity.GameState;
using UltraLiteDB;


namespace Impunity.Connection
{

	// Internal type info

	/// <summary>Internal metadata for a single distributed field: ID, serialization methods, persistence info.</summary>
	public class DistributedTypeFieldInfo
	{
		/// <summary>The field's unique id (1–63), matching its <c>[Distributed(id)]</c> attribute.</summary>
		public byte FieldId;
		/// <summary>Precomputed dirty-bit mask for this field: <c>1UL &lt;&lt; (FieldId - 1)</c>.</summary>
		public UInt64 FieldBitmask;
		/// <summary>The CLR field name on the entity type.</summary>
		public string FieldName;
		/// <summary>The database key from <c>[Distributed(PersistAs=…)]</c>, or null if the field is not persisted.</summary>
		public string? PersistedAs;
		/// <summary>True if the field is temporal (a transient value; never persisted).</summary>
		public bool IsTemporal;
		/// <summary>The container kind: Value, Array, Queue, IntDictionary, or StringDictionary.</summary>
		public GameStateEntityFieldType FieldType;
		/// <summary>The wire value type, including Custom/CustomSmall (and nullable variants) for complex values.</summary>
		public GameStateEntityPropertyValueType FieldValueType;
		/// <summary>The CLR value type the field carries (the <c>T</c> of <c>DistributedValue&lt;T,S&gt;</c>; the element/value type for collections).</summary>
		public Type ValueClrType = null!;
		/// <summary>Generated wrapper that serializes the field's dirty changes to a <see cref="BinaryWriter"/>.</summary>
		public MethodInfo WriteMethod;
		/// <summary>Generated wrapper that reads the field's full initial value from a <see cref="BinaryReader"/>.</summary>
		public MethodInfo InitMethod;
		/// <summary>Generated wrapper that reads and applies a delta change for the field from a <see cref="BinaryReader"/>.</summary>
		public MethodInfo UpdateMethod;
		/// <summary>Generated wrapper that reads past a field's encoded bytes without applying them (used to discard stale updates).</summary>
		public MethodInfo SkipMethod;
		/// <summary>Generated wrapper that returns the field's current value as a <see cref="BsonValue"/> for persistence.</summary>
		public MethodInfo GetAsBsonMethod;
		/// <summary>Generated wrapper that sets the field's value from a persisted <see cref="BsonValue"/>.</summary>
		public MethodInfo SetFromBsonMethod;

		/// <summary>Creates field metadata from the field's <c>[Distributed]</c> attribute, resolved value/container
		/// types, and the entity type's generated serialization wrapper methods.</summary>
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
		/// <summary>The entity type's distributed id, from its <c>[DistributedEntity(id)]</c> attribute.</summary>
		public int DistributedTypeId;
		/// <summary>True if the type implements <see cref="IDistributedChannel"/> (a channel rather than an object).</summary>
		public bool IsChannel;
		/// <summary>True if the type is persisted (its <c>[DistributedEntity]</c> has a <c>PersistAs</c> key).</summary>
		public bool Persisted;
		/// <summary>The CLR type of the entity.</summary>
		public Type ObjectType;
		/// <summary>Optional factory delegate (from <c>[DistributedEntity(FactoryMethod=…)]</c>) used to construct
		/// instances instead of <c>Activator.CreateInstance</c>; null to use the default constructor.</summary>
		public Func<IDistributedEntity>? Factory;
		/// <summary>Per-field metadata indexed by field id (slot 0 and any gaps are null), or null if the type has no
		/// distributed fields.</summary>
		public DistributedTypeFieldInfo[] DistributedFields = null!;

		/// <summary>Creates type metadata for a registered distributed entity type. <see cref="Factory"/>,
		/// <see cref="IsChannel"/>, and <see cref="DistributedFields"/> are populated separately during registration.</summary>
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

		/// <summary>Creates an entity manager with empty registries and pre-allocated property (de)serialization buffers.
		/// Assign <see cref="Connection"/> and call <see cref="RegisterEntityTypes"/> before use; a connection normally
		/// owns and wires up its own manager.</summary>
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

		public IReadOnlyList<IDistributedChannel> CurrentSubscriptions
		{
			get => SubscribedChannels.Values.ToList();
		}

		/// <summary>Registers all distributed entity types via reflection. Builds internal type metadata and returns
		/// format definitions for the server handshake. Throws if two types share a distributed id or a
		/// <c>PersistAs</c> key.</summary>
		/// <param name="entityTypes">The <c>[DistributedEntity]</c>-annotated types this client will use. May be null or
		/// empty, in which case no types are registered and null is returned.</param>
		/// <returns>The wire format definitions to send to the server during the handshake, or null when
		/// <paramref name="entityTypes"/> is null or empty.</returns>
		public GameStateEntityTypeDef[]? RegisterEntityTypes(Type[] entityTypes)
		{
			if (entityTypes == null || entityTypes.Length == 0)
			{
				DistributedTypes = new DistributedTypeInfo[1];
				return null;
			}

			List<DistributedTypeInfo> internalTypeInfoList = new List<DistributedTypeInfo>();

			GameStateEntityTypeDef[] convertedEntityTypes = new GameStateEntityTypeDef[entityTypes.Length];

			// PersistAs is the durable type identity stored with every persisted entity, so it must be
			// unique across types
			Dictionary<string, string> persistKeyOwners = new Dictionary<string, string>();

			int i = 0;
			foreach (Type entityType in entityTypes)
			{
				GameStateEntityTypeDef entityData = RegisterEntityType(entityType, internalTypeInfoList);
				convertedEntityTypes[i++] = entityData;

				if (entityData.PersistedAs != null)
				{
					if (persistKeyOwners.TryGetValue(entityData.PersistedAs, out string otherTypeName))
					{
						throw new Exception("Entity types " + otherTypeName + " and " + entityData.Name + " both use PersistAs key '" + entityData.PersistedAs + "'");
					}
					persistKeyOwners[entityData.PersistedAs] = entityData.Name;
				}
			}

			Array.Sort(convertedEntityTypes,
				(e1, e2) =>
				{
					return e1.Index - e2.Index;
				}
			);

			int maxEntityIndex = convertedEntityTypes[convertedEntityTypes.Length - 1].Index;

			DistributedTypes = new DistributedTypeInfo[maxEntityIndex + 1];
			foreach (DistributedTypeInfo dtinfo in internalTypeInfoList)
			{
				if (DistributedTypes[dtinfo.DistributedTypeId] != null)
				{
					throw new Exception("More than one type using distributed id " + dtinfo.DistributedTypeId);
				}

				DistributedTypes[dtinfo.DistributedTypeId] = dtinfo;
			}


			return convertedEntityTypes;
		}


		/// <summary>Creates a distributed object in a channel. Serializes the object's initial properties and sends them
		/// to the server, then registers the object locally once the server confirms. Before invoking
		/// <paramref name="onComplete"/> the creating client receives the same creation notifications a replicated
		/// object would (<see cref="OnDistributedObjectCreated"/>, the channel's <see cref="IDistributedChannel.OnObjectAdded"/>
		/// with <c>newlyCreated: true</c>, and the object's <see cref="IDistributedEntity.OnFullyInitialized"/>) — the
		/// server does not echo the create back to its originator, so these are raised locally instead. The object's
		/// existing field values are kept as-is (they are not re-applied from the wire).
		/// Throws if the manager has no <see cref="Connection"/>, the unique name contains '/', or the persisted/client-
		/// authoritative flags are inconsistent with the type or channel.</summary>
		/// <typeparam name="T">The concrete distributed object type being created.</typeparam>
		/// <param name="distObj">The object instance to create; its distributed fields supply the initial property values.</param>
		/// <param name="channel">The channel to create the object in.</param>
		/// <param name="replace">If true, replace any existing object with the same unique name in the channel.</param>
		/// <param name="onComplete">Receives the same <paramref name="distObj"/> (now registered) on success, or a non-null
		/// error with a null object on failure. May be null.</param>
		public void CreateObject<T>(T distObj, IDistributedChannel channel, bool replace, ImpunityCallback<T> onComplete) where T : class, IDistributedObject
		{
			if (Connection == null)
			{
				throw new Exception("ClientEntityManager has no connection");
			}

			if (distObj.UniqueName != null && distObj.UniqueName.Contains("/"))
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
			if (distObj.DeleteOnDisconnect)
			{
				instanceFlags |= (byte)ImpunityInstanceFlags.DeleteOnDisconnect;

				if (DistributedTypes[entityTypeId].Persisted)
				{
					throw new Exception("Can't create a delete on disconnect object that is also persisted");
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

			Connection.CreateObject(entityTypeId, instanceFlags, channel.DistributedEntityId, propertyBytes, distObj.UniqueName, replace, (ImpunityErrorResponse? err, uint objectId) =>
			{
				if (err != null)
				{
					onComplete?.Invoke(err, null!);
					return;
				}

				RegisterEntity(distObj, objectId);

				distObj.Channel = channel;

				try
				{
					OnDistributedObjectCreated?.Invoke(distObj, channel, true);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception in OnDistributedObjectCreated handler:", e);
				}

				try
				{
					channel.OnObjectAdded(distObj, true);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception in channel OnObjectAdded:", e);
				}

				try
				{
					distObj.OnFullyInitialized();
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception in OnFullyInitialized:", e);
				}

				onComplete?.Invoke(err, distObj);
			});
		}

		private ObjectCreateData MakeObjectCreateData(IDistributedObject distObj, IDistributedChannel channel)
		{
			ObjectCreateData data = new ObjectCreateData();

			if (distObj.UniqueName != null && distObj.UniqueName.Contains("/"))
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
			if (distObj.DeleteOnDisconnect)
			{
				instanceFlags |= (byte)ImpunityInstanceFlags.DeleteOnDisconnect;

				if (DistributedTypes[entityTypeId].Persisted)
				{
					throw new Exception("Can't create a delete on disconnect object that is also persisted");
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

		/// <summary>Creates a distributed channel, optionally pre-populated with child objects, and sends it to the
		/// server. Note this does not subscribe the caller to the channel — use <see cref="SubscribeToChannel"/> for that.
		/// Throws if the manager has no <see cref="Connection"/> or the persisted/client-authoritative flags are
		/// inconsistent with the type.</summary>
		/// <typeparam name="T">The concrete distributed channel type being created.</typeparam>
		/// <param name="channelName">The channel's unique name; assigned to <paramref name="channel"/>'s Name.</param>
		/// <param name="channel">The channel instance to create; its distributed fields supply the initial property values.</param>
		/// <param name="replace">If true, replace any existing channel with the same name.</param>
		/// <param name="channelObjects">Optional initial child objects to create within the channel; may be null.</param>
		/// <param name="onComplete">Receives true on success, or a non-null error on failure. May be null.</param>
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
			if (channel.DeleteOnDisconnect)
			{
				instanceFlags |= (byte)ImpunityInstanceFlags.DeleteOnDisconnect;

				if (DistributedTypes[entityTypeId].Persisted)
				{
					throw new Exception("Can't create a delete on disconnect object that is also persisted");
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

				foreach (IDistributedObject distObj in channelObjects)
				{
					objectCreateList.Add(MakeObjectCreateData(distObj, channel));
				}
			}

			Connection.CreateChannel(channelName, entityTypeId, instanceFlags, propertyBytes, replace, objectCreateList, onComplete);
		}

		/// <summary>Subscribes to a channel by name and delivers the live channel instance via the callback. If already
		/// subscribed, returns the existing channel immediately. Pass a non-null <paramref name="createIfNeeded"/> to
		/// create the channel if it doesn't already exist on the server. Throws if the manager has no
		/// <see cref="Connection"/>, the name is null, or the name contains '/'.</summary>
		/// <typeparam name="T">The concrete distributed channel type expected back.</typeparam>
		/// <param name="channelName">The name of the channel to subscribe to.</param>
		/// <param name="createIfNeeded">A channel instance used to create the channel if it doesn't exist (its fields
		/// supply the initial properties); pass null to only subscribe to an existing channel.</param>
		/// <param name="onComplete">Receives the subscribed channel on success, or a non-null error with a null channel
		/// on failure. May be null (though the already-subscribed fast path invokes it unconditionally).</param>
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
			byte instanceFlags = 0;
			ArraySegment<byte> propertyBytes = null;


			if (createIfNeeded != null)
			{
				createIfMising = true;

				createIfNeeded.Name = channelName;

				entityTypeId = createIfNeeded.DistributedEntityType;

				propertyBytes = GetPropertyBytes(createIfNeeded, out _);

				if (createIfNeeded.IsClientAuthoritative)
				{
					instanceFlags |= (byte)ImpunityInstanceFlags.ClientAuthoritative;

					if (DistributedTypes[entityTypeId].Persisted)
					{
						throw new Exception("Can't create a client authoritative channel that is also persisted");
					}
				}
				if (createIfNeeded.DeleteOnDisconnect)
				{
					instanceFlags |= (byte)ImpunityInstanceFlags.DeleteOnDisconnect;

					if (DistributedTypes[entityTypeId].Persisted)
					{
						throw new Exception("Can't create a delete on disconnect object that is also persisted");
					}
				}
				if (createIfNeeded.IsPersisted)
				{
					if (!DistributedTypes[entityTypeId].Persisted)
					{
						throw new Exception("Can't create persisted channel of a type that is not persistant");
					}

					instanceFlags |= (byte)ImpunityInstanceFlags.Persisted;
				}
			}

			Connection.SubcribeToChannel(channelName, createIfMising, entityTypeId, instanceFlags, propertyBytes, null, (ImpunityErrorResponse? err, uint channelId) =>
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
		/// Throws if the manager has no <see cref="Connection"/>.
		/// </summary>
		/// <param name="channel">The subscribed channel to unsubscribe from.</param>
		/// <param name="onComplete">Invoked when the server acknowledges the unsubscribe; the error argument is non-null on failure. May be null.</param>
		/// <param name="immediate">If true, tear down the channel and its objects synchronously and suppress further updates instead of waiting for the server ack.</param>
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
			if (entityData.Index <= 0)
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
						if (fieldInfo.DeclaringType == entityType)
						{
							throw new Exception("Can't have a distributed field persisted if the entity is not persisted");
						}

						// A persisted field inherited from a base type: this type is not itself
						// persisted, so the field is replicated-only here.
						fieldPersistedAs = null;
					}
					else
					{
						if (isTemporalValue)
						{
							throw new Exception("A temporal field can't be persisted");
						}

						hasPersistedField = true;
					}
				}

				MethodInfo? writeMethod = GetTypeMethodInherited(entityType, "_imp_WriteChangesWrapper_" + fieldInfo.Name, BindingFlags.Instance | BindingFlags.NonPublic);
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


				DistributedTypeFieldInfo dfield = new DistributedTypeFieldInfo(fieldAttr.FieldId, fieldBitmask, fieldInfo.Name, fieldPersistedAs,
																				isTemporalValue, tempFieldValue.FieldType, tempFieldValue.ValueType,
																				writeMethod, initMethod, updateMethod, skipMethod, getAsBsonMethod, setFromBsonMethod);

				// The field's first generic argument is the value CLR type (T), i.e. the element type
				// for arrays/queues and the value type for dictionaries.
				dfield.ValueClrType = fieldType.GetGenericArguments()[0];

				distributedFields.Add(dfield);
			}

			if (distributedFields.Count == 0)
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


		/// <summary>Handles a channel-create push from the server: instantiates the channel (via the type's factory,
		/// <c>Activator</c>, or a <see cref="GenericDistributedChannel"/> when <paramref name="channelType"/> is 0),
		/// registers it, applies its initial properties, and fires <see cref="IDistributedEntity.OnFullyInitialized"/>.
		/// Also clears any lingering immediate-unsubscribe suppression for this id (a fresh create means a re-subscribe).
		/// Invoked by the connection's server-message dispatch on the main thread; not called by application code.</summary>
		/// <param name="channelId">The server-assigned entity id of the channel.</param>
		/// <param name="channelName">The channel's name.</param>
		/// <param name="channelType">The distributed type id of the channel, or 0 for a generic untyped channel.</param>
		/// <param name="isLocked">Whether the channel is currently locked on the server.</param>
		/// <param name="instanceFlags">Packed <see cref="ImpunityInstanceFlags"/> (e.g. persisted) for the channel.</param>
		/// <param name="propData">The serialized initial property values for the channel.</param>
		/// <param name="seq">The channel's current broadcast seq at snapshot time; seeds all per-field received-seqs so
		/// optimistic exclusive updates from this fresh subscriber aren't rejected against pre-subscribe modifications.</param>
		public void HandleCreateChannel(uint channelId, string channelName, int channelType, bool isLocked, byte instanceFlags, ArraySegment<byte> propData, ushort seq)
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
			SeedFieldRecvSeq(channel, seq);

			SetPropertyBytes(channel, propData, true);

			try
			{
				channel.OnFullyInitialized();
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception in OnFullyInitialized:", e);
			}
		}

		/// <summary>Handles an object-create push from the server: instantiates the object, registers it, fires
		/// <see cref="OnDistributedObjectCreated"/> and the channel's <see cref="IDistributedChannel.OnObjectAdded"/>,
		/// applies its initial properties, and fires <see cref="IDistributedEntity.OnFullyInitialized"/>. If the owning
		/// channel is mid immediate-unsubscribe, the object is dropped and its id suppressed instead. Invoked by the
		/// connection's server-message dispatch on the main thread; not called by application code.</summary>
		/// <param name="objectId">The server-assigned entity id of the object.</param>
		/// <param name="channelId">The entity id of the channel the object belongs to.</param>
		/// <param name="objectType">The distributed type id of the object.</param>
		/// <param name="isLocked">Whether the object is currently locked on the server.</param>
		/// <param name="instanceFlags">Packed <see cref="ImpunityInstanceFlags"/> (e.g. persisted) for the object.</param>
		/// <param name="propData">The serialized initial property values for the object.</param>
		/// <param name="uniqueName">The object's unique name within the channel, or null if anonymous.</param>
		/// <param name="newlyCreated">True if the object was just created while subscribed; false if it is part of the
		/// initial snapshot delivered when the channel was first received.</param>
		/// <param name="seq">The object's current broadcast seq at snapshot time; seeds all per-field received-seqs so
		/// optimistic exclusive updates from this fresh subscriber aren't rejected against pre-subscribe modifications.</param>
		public void HandleCreateObject(uint objectId, uint channelId, int objectType, bool isLocked, byte instanceFlags, ArraySegment<byte> propData, string? uniqueName, bool newlyCreated, ushort seq)
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
			SeedFieldRecvSeq(entity, seq);

			IDistributedChannel? channel = DistributedObjects[channelId] as IDistributedChannel;

			if (channel == null)
			{
				throw new Exception("No channel with id " + channelId);
			}

			entity.Channel = channel;

			try
			{
				OnDistributedObjectCreated?.Invoke(entity, channel, newlyCreated);
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception in OnDistributedObjectCreated handler:", e);
			}

			try
			{
				channel.OnObjectAdded(entity, newlyCreated);
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception in channel OnObjectAdded:", e);
			}

			SetPropertyBytes(entity, propData, true);

			try
			{
				entity.OnFullyInitialized();
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception in OnFullyInitialized:", e);
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

		/// <summary>Seeds every entry of a freshly-registered entity's <see cref="IDistributedEntity.FieldRecvSeq"/> with the
		/// entity's current server broadcast seq (from its create message). Without this, a new subscriber's zero-valued
		/// received-seqs would be behind the server's per-field last-modified seq for any field changed before this client
		/// subscribed, causing its optimistic exclusive updates to be rejected forever with no broadcast to correct them.</summary>
		private static void SeedFieldRecvSeq(IDistributedEntity entity, ushort seq)
		{
			if (seq != 0 && entity.FieldRecvSeq != null)
			{
				Array.Fill(entity.FieldRecvSeq, seq);
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

		/// <summary>Handles an entity property-update push from the server, applying the delta to the entity's
		/// distributed fields. Per-field sequence numbers discard stale out-of-order updates. Updates for entities
		/// suppressed by an immediate unsubscribe are dropped; an update for an unknown entity is logged and ignored.
		/// Invoked by the connection's server-message dispatch on the main thread; not called by application code.</summary>
		/// <param name="entityId">The entity id the update applies to.</param>
		/// <param name="updateData">The serialized delta of changed property values.</param>
		/// <param name="seq">The server's outgoing sequence number for this update, used for stale-update detection.</param>
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

		/// <summary>Handles an entity event push from the server by dispatching it to the entity's
		/// <see cref="IDistributedEntity.OnEventTriggered"/>. Events for suppressed entities are dropped; an event for an
		/// unknown entity is logged and ignored. Invoked by the connection's server-message dispatch on the main thread;
		/// not called by application code.</summary>
		/// <param name="entityId">The entity id the event was fired on.</param>
		/// <param name="eventType">The application-defined event type id.</param>
		/// <param name="eventData">The BSON payload delivered with the event.</param>
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

		/// <summary>Handles an entity-locked push from the server: sets <see cref="IDistributedEntity.IsLocked"/> true and
		/// fires <see cref="IDistributedEntity.OnLocked"/>. Locks for suppressed entities are dropped; a lock for an
		/// unknown entity is logged and ignored. Invoked by the connection's server-message dispatch on the main thread;
		/// not called by application code.</summary>
		/// <param name="entityId">The entity id that became locked.</param>
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
			try
			{
				entity.OnLocked();
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception in OnLocked: ", e);
			}
		}

		/// <summary>Handles an entity-unlocked push from the server: sets <see cref="IDistributedEntity.IsLocked"/> false
		/// and fires <see cref="IDistributedEntity.OnUnlocked"/> (which also completes any pending <c>WaitForLock</c>
		/// callbacks). Unlocks for suppressed entities are dropped; an unlock for an unknown entity is logged and ignored.
		/// Invoked by the connection's server-message dispatch on the main thread; not called by application code.</summary>
		/// <param name="entityId">The entity id that became unlocked.</param>
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
			try
			{
				entity.OnUnlocked();
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception in OnUnlocked: ", e);
			}
		}

		/// <summary>Handles an entity-delete push from the server. Unregisters the entity and fires its
		/// <see cref="IDistributedEntity.OnDeleted"/> then <see cref="IDistributedEntity.OnUndistributed"/>; if it is a
		/// channel, each member is first unregistered and given <see cref="IDistributedEntity.OnUndistributed"/>. Deletes
		/// for suppressed entities are dropped; a delete for an unknown entity is logged and ignored. Invoked by the
		/// connection's server-message dispatch on the main thread; not called by application code.
		/// If the deleted entity is an object, its owning channel is notified via
		/// <see cref="IDistributedChannel.OnObjectRemoved"/>.</summary>
		/// <param name="entityId">The entity id that was deleted.</param>
		/// <param name="deleteData">Optional BSON payload supplied by the deleter, relayed to <see cref="IDistributedEntity.OnDeleted"/>; may be null.</param>
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
				foreach (var member in channel.DistributedObjects.Values)
				{
					UnregisterEntity(member);

					try
					{
						member.OnUndistributed();
					}
					catch (Exception e)
					{
						ImpunityLogger.LogError("Exception in OnUndistributed: ", e);
					}
				}
			}
			if (entity is IDistributedObject obj)
			{
				try
				{
					obj.Channel?.OnObjectRemoved(obj);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception in channel's OnObjectRemoved: ", e);
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
			try
			{
				entity.OnUndistributed();
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception in OnUndistributed: ", e);
			}
		}

		/// <summary>Marks an entity as having dirty properties that need to be sent to the server on the next <see cref="SendUpdates"/> call.
		/// Called by an entity's own <see cref="IDistributedEntity.SetDirty"/>; not normally called by application code directly.</summary>
		/// <param name="entity">The entity with pending changes to flush on the next update.</param>
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

		/// <summary>Serializes an entity's changed (or all) distributed fields into the manager's shared encoding buffer
		/// and clears the entity's dirty state. The returned segment points into that reused buffer, so it is only valid
		/// until the next call that writes to the buffer — consume or copy it before then.</summary>
		/// <param name="entity">The entity whose fields to serialize.</param>
		/// <param name="guaranteed">Receives the entity's <see cref="IDistributedEntity.DirtyGuaranteed"/> flag, i.e.
		/// whether the produced update must be delivered reliably.</param>
		/// <param name="allProperties">If true, serialize every field regardless of dirty bits (used for the full initial
		/// state on create); if false (default), serialize only the currently dirty fields.</param>
		/// <returns>An <see cref="ArraySegment{T}"/> over the shared buffer holding the encoded property bytes, or a
		/// default/null segment when there is nothing to send.</returns>
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

		/// <summary>Deserializes a property byte buffer onto an entity's distributed fields. When
		/// <paramref name="initialRead"/> is true each field is read as a full initial value; otherwise each field is
		/// applied as a delta, and the per-field sequence number is used to skip stale out-of-order updates. A null or
		/// empty buffer is a no-op. Throws on an invalid property id in the buffer.</summary>
		/// <param name="entity">The entity to apply the property values to.</param>
		/// <param name="propertyBytes">The serialized property bytes (full values or a delta).</param>
		/// <param name="initialRead">True to read full initial values; false to apply a delta update.</param>
		/// <param name="seq">The update's sequence number for stale-update detection on delta reads; ignored (use 0) for initial reads.</param>
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

		/// <summary>Immediately flushes an entity's pending dirty fields as an exclusive (optimistic-concurrency) update,
		/// bypassing the per-frame dirty sweep. The update carries the client's known per-field seqs; the server rejects the
		/// whole update (nothing applied) with <see cref="ImpunityErrorCode.ActionStaleData"/> if any field changed since this
		/// client last saw it, or <see cref="ImpunityErrorCode.ActionBlockedByLock"/> if another connection holds the lock.
		/// On success and on rejection the winning values are already applied locally by the time <paramref name="onComplete"/>
		/// runs (see the ordering guarantee in docs/guides/DistributedEntities.md). Backs <see cref="IDistributedEntity.UpdateExclusive"/>.</summary>
		/// <param name="entity">The entity whose pending dirty fields to flush exclusively.</param>
		/// <param name="onComplete">Callback invoked with null on success or an error on rejection. Delivered on a later
		/// <see cref="BaseGameConnection.Update"/>, never reentrantly.</param>
		public void SendEntityUpdatesExclusive(IDistributedEntity entity, ImpunityCallback? onComplete)
		{
			if (Connection == null)
			{
				throw new Exception("ClientEntityManager has no connection");
			}

			if (entity.Manager != this || entity.DistributedEntityId == 0 || !DistributedObjects.ContainsKey(entity.DistributedEntityId))
			{
				Connection.QueueLocalCallback(onComplete, new ImpunityErrorResponse(ImpunityErrorCode.ActionBadRequest, "Entity is not registered with this connection"));
				return;
			}

			// Snapshot dirty bits before GetPropertyBytes clears them; nothing dirty is trivially a success.
			ulong dirtyBits = entity.DirtyBits;
			if (dirtyBits == 0)
			{
				Connection.QueueLocalCallback(onComplete, null);
				return;
			}

			DistributedTypeInfo typeInfo = GetRegisteredTypeInfo(entity);

			// Build the known-seqs blob [fieldId][seq lo][seq hi]...[0] for exactly the dirty fields, using this
			// client's last-received seq per field (0 if never received). Must be gathered before dirty state is cleared.
			int dirtyCount = 0;
			foreach (var fieldInfo in typeInfo.DistributedFields)
			{
				if (fieldInfo != null && (dirtyBits & fieldInfo.FieldBitmask) != 0)
				{
					dirtyCount++;
				}
			}

			byte[] seqBlob = new byte[dirtyCount * 3 + 1];
			int pos = 0;
			foreach (var fieldInfo in typeInfo.DistributedFields)
			{
				if (fieldInfo == null || (dirtyBits & fieldInfo.FieldBitmask) == 0)
				{
					continue;
				}

				ushort known = entity.FieldRecvSeq != null ? entity.FieldRecvSeq[fieldInfo.FieldId] : (ushort)0;
				seqBlob[pos++] = fieldInfo.FieldId;
				seqBlob[pos++] = (byte)(known & 0xFF);
				seqBlob[pos++] = (byte)((known >> 8) & 0xFF);
			}
			seqBlob[pos] = 0;

			// Serialize the field deltas, then copy out of the shared reused encode buffer: this update must not alias
			// that buffer, since the local in-process connection shares it by reference and the remote writer thread
			// drains it asynchronously.
			ArraySegment<byte> propBytes = GetPropertyBytes(entity, out _);
			byte[] updateCopy = propBytes.Array != null ? new byte[propBytes.Count] : Array.Empty<byte>();
			if (propBytes.Array != null)
			{
				Array.Copy(propBytes.Array, propBytes.Offset, updateCopy, 0, propBytes.Count);
			}

			// Drop from the per-frame sweep so it doesn't also send a now-empty follow-up update.
			DirtyObjects.Remove(entity);

			entity.SendSeq++;
			Connection.UpdateEntityExclusive(entity.DistributedEntityId, updateCopy, seqBlob, entity.SendSeq, onComplete);
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

		/// <summary>Builds a <see cref="BsonDocument"/> of the entity's persisted distributed fields, keyed by each
		/// field's <c>PersistAs</c> name. Non-persisted fields are skipped. Useful for storing an entity's state locally
		/// (e.g. an offline/editor instance) using the same keys the server persists under. Throws if the entity's type
		/// is not registered with this manager.</summary>
		/// <param name="entity">The entity whose persisted fields to serialize.</param>
		/// <returns>A document mapping each persisted field's <c>PersistAs</c> key to its current value.</returns>
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

		/// <summary>Applies persisted field values from a <see cref="BsonDocument"/> (as produced by
		/// <see cref="GetPersistedFieldsAsBson"/>) back onto an entity, matching each persisted field by its
		/// <c>PersistAs</c> key. Missing or null entries are left unchanged. Throws if the entity's type is not
		/// registered with this manager.</summary>
		/// <param name="entity">The entity to populate.</param>
		/// <param name="doc">The document holding persisted field values keyed by <c>PersistAs</c> name.</param>
		public void ApplyPersistedFieldsFromBson(IDistributedEntity entity, BsonDocument doc)
		{
			DistributedTypeInfo typeInfo = GetRegisteredTypeInfo(entity);

			foreach (var fieldInfo in typeInfo.DistributedFields)
			{
				if (fieldInfo == null) continue;

				if (fieldInfo.PersistedAs == null) continue;
				BsonValue fieldValue = doc[fieldInfo.PersistedAs];
				if (fieldValue == null || fieldValue.IsNull) continue;

				object[] parameters = new object[] { fieldValue };
				fieldInfo.SetFromBsonMethod.Invoke(entity, parameters);
			}
		}

		/// <summary>
		/// Returns a read-only description of every distributed field on a registered entity type, for tools
		/// that need to enumerate and classify fields (id, name, persistence key, container kind, value type,
		/// and CLR value type). Throws if <paramref name="entityType"/> is not registered with this manager.
		/// </summary>
		/// <param name="entityType">The registered <c>[DistributedEntity]</c> type to describe.</param>
		/// <returns>A read-only list of <see cref="DistributedFieldInfo"/>, one per distributed field; empty if the type
		/// has no distributed fields.</returns>
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
