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
		/// <summary>The database key from <c>[PersistAs("…")]</c>, or null if the field is not persisted.</summary>
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
		/// <summary>Generated wrapper that serializes the field's complete state (pending changes included) and returns
		/// false, having written nothing, when the field holds nothing to send. Used for creates.</summary>
		public MethodInfo FullStateWriteMethod;
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

		/// <summary>Creates field metadata from the field's assigned wire id, resolved value/container
		/// types, and the entity type's generated serialization wrapper methods.</summary>
		public DistributedTypeFieldInfo(byte fieldId, UInt64 fieldBitmask, string fieldName, string? persistedAs, bool isTemporal,
			GameStateEntityFieldType fieldType, GameStateEntityPropertyValueType fieldValueType,
			MethodInfo writeMethod, MethodInfo fullStateWriteMethod, MethodInfo initMethod, MethodInfo updateMethod, MethodInfo skipMethod, MethodInfo getAsBsonMethod, MethodInfo setFromBsonMethod)
		{
			this.FieldId = fieldId;
			this.FieldBitmask = fieldBitmask;
			this.FieldName = fieldName;
			this.PersistedAs = persistedAs;
			this.IsTemporal = isTemporal;
			this.FieldType = fieldType;
			this.FieldValueType = fieldValueType;
			this.WriteMethod = writeMethod;
			this.FullStateWriteMethod = fullStateWriteMethod;
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
		/// <summary>Per-field metadata indexed by field id (slot 0 and any gaps are null). Never null: a type with no
		/// distributed fields keeps an empty array, so every walk over the fields is a no-op for it.</summary>
		public DistributedTypeFieldInfo[] DistributedFields = Array.Empty<DistributedTypeFieldInfo>();

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
		/// <summary>The persistence key from <c>[PersistAs("…")]</c>, or null if the field is not persisted.</summary>
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
	public partial class ClientEntityManager
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
		/// <param name="onCreatedAction">Optional database action sent in the same message and run by the server only if
		/// the object is created — e.g. removing the item from the player's inventory as it is dropped into the world.
		/// Its own callback fires after <paramref name="onComplete"/>, so the object is already registered by then. See
		/// <see cref="BaseGameConnection.CreateObject"/>. If this method throws, the action was never taken and is left
		/// unresolved: the caller still owns it, and should resolve it (<see cref="GameStateActionBase.SkipUnsent"/>) if
		/// something is waiting on its callback.</param>
		/// <param name="replaceEntityId">Non-zero: replace exactly this entity, which must hold the object's unique name,
		/// or fail without changing anything (see <see cref="BaseGameConnection.CreateObject"/>). Implies
		/// <paramref name="replace"/>.</param>
		/// <param name="replaceGuard">Non-zero: a guarded replace. The fields of the entity being replaced that the
		/// replace depends on (their <c>Guard</c>s combined with <c>|</c>); the create fails with
		/// <see cref="ImpunityErrorCode.ActionStaleData"/>, changing nothing and skipping the conditional, if any of them
		/// changed since this client last received them. Needs <paramref name="replaceEntityId"/>, and that entity must be
		/// registered with this manager (subscribed), since the guard is this client's view of it. A lock holder on the
		/// replaced entity bypasses the check.</param>
		/// <param name="replaceData">Why the existing object is being replaced: relayed to every subscriber in the replaced
		/// entity's <see cref="IDistributedEntity.OnDeleted"/> (<see cref="EntityDeletion.Data"/>), as <c>deleteData</c>
		/// is for a delete. Null for none; unused when nothing is replaced.</param>
		public void CreateObject<T>(T distObj, IDistributedChannel channel, bool replace, ImpunityCallback<T> onComplete, GameStateActionBase? onCreatedAction = null, uint replaceEntityId = 0, FieldGuard replaceGuard = default,
			BsonValue? replaceData = null) where T : class, IDistributedObject
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

			ArraySegment<byte> propertyBytes = GetPropertyBytes(distObj, out _, allProperties: true);

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

			if (!replaceGuard.IsEmpty)
			{
				// The guard knows which entity it is about, so the id need not be passed as well.
				uint guardedId = replaceGuard.Entity!.DistributedEntityId;
				if (replaceEntityId == 0)
				{
					replaceEntityId = guardedId;
				}
				else if (replaceEntityId != guardedId)
				{
					throw new ArgumentException("The guard names fields of entity " + guardedId + ", not of replaceEntityId " + replaceEntityId, nameof(replaceGuard));
				}
			}

			if (replaceEntityId != 0 && distObj.UniqueName == null)
			{
				throw new Exception("A conditional replace needs the object's unique name");
			}

			byte[]? replaceGuardBlob = null;
			if (!replaceGuard.IsEmpty)
			{
				if (replaceEntityId == 0 || !DistributedObjects.TryGetValue(replaceEntityId, out IDistributedEntity? replaced))
				{
					throw new ArgumentException("Entity " + replaceEntityId + " is not registered with this manager, so there is no view of it to guard", nameof(replaceGuard));
				}

				replaceGuardBlob = BuildGuardBlob(replaced, replaceGuard.MaskFor(replaced, nameof(replaceGuard), true), nameof(replaceGuard));
			}

			ImpunityCallback<uint> onCreated = (ImpunityErrorResponse? err, uint objectId) =>
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
			};

			Connection.DoAction(new CreateObjectAction(entityTypeId, instanceFlags, channel.DistributedEntityId, propertyBytes, distObj.UniqueName,
				replace || replaceEntityId != 0, onCreated, onCreatedAction, replaceEntityId)
			{
				ReplaceKnownFieldSeqs = replaceGuardBlob != null ? new ArraySegment<byte>(replaceGuardBlob) : default,
				ReplaceData = replaceData,
				SentServerMs = Connection.GetServerTime(),
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

			ArraySegment<byte> propertyBytes = GetPropertyBytes(distObj, out _, allProperties: true);

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

			ArraySegment<byte> propertyBytes = GetPropertyBytes(channel, out _, allProperties: true);
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

				propertyBytes = GetPropertyBytes(createIfNeeded, out _, allProperties: true);

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

			// Wire ids are assigned from the type itself, over its whole inheritance chain. The generated
			// InitializeDistributedFields resolves each field's bitmask through the same table, so the two agree
			// without either of them naming a number.
			Dictionary<string, byte> fieldIds = DistributedFieldIds.ForType(entityType);

			IEnumerable<FieldInfo> distributedFieldInfos = GetDistributedFields(entityType);
			foreach (var fieldInfo in distributedFieldInfos)
			{

				if (!fieldIds.TryGetValue(fieldInfo.Name, out byte assignedFieldId))
				{
					throw new Exception("No wire id assigned for distributed field " + entityType.Name + "."
						+ fieldInfo.Name);
				}

				Type fieldType = fieldInfo.FieldType;
				bool isTemporalValue = fieldType.GetInterface(nameof(IDistributedTemporalField)) != null;


				// Create a throw-away instance so we can get its type info
				IDistributedField tempFieldValue = (IDistributedField)Activator.CreateInstance(fieldType);

				var fieldBitmask = 1ul << (assignedFieldId - 1);
				PersistAs persistAttr = (PersistAs)fieldInfo.GetCustomAttribute(typeof(PersistAs));
				var fieldPersistedAs = persistAttr?.Key?.Trim();

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

				MethodInfo? fullStateWriteMethod = GetTypeMethodInherited(entityType, "_imp_WriteFullStateWrapper_" + fieldInfo.Name, BindingFlags.Instance | BindingFlags.NonPublic);
				if (fullStateWriteMethod == null)
				{
					throw new Exception("Cant find full state write method for property " + fieldInfo.Name + " on type " + entityType.Name + " (is ImpunityCodeGenerator.dll out of date?)");
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


				DistributedTypeFieldInfo dfield = new DistributedTypeFieldInfo(assignedFieldId, fieldBitmask, fieldInfo.Name, fieldPersistedAs,
																				isTemporalValue, tempFieldValue.FieldType, tempFieldValue.ValueType,
																				writeMethod, fullStateWriteMethod, initMethod, updateMethod, skipMethod, getAsBsonMethod, setFromBsonMethod);

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

		/// <summary>Returns every distributed field on a type, including inherited ones, in wire-id order.
		/// <para>The inheritance chain is walked one level at a time with <see cref="BindingFlags.DeclaredOnly"/>
		/// rather than relying on a flattened <c>GetFields</c>, for two reasons: a flattened call silently omits a
		/// base type's <c>private</c> fields (which would leave them holding a dirty bit that nothing ever
		/// serializes), and its ordering is not contractual. This mirrors
		/// <see cref="DistributedFieldIds"/> exactly, so registration and the generated field initializers can
		/// never disagree about which fields exist or what they are numbered.</para></summary>
		private static IEnumerable<FieldInfo> GetDistributedFields(Type typeInfo)
		{
			List<Type> chain = new List<Type>();
			for (Type level = typeInfo; level != null && level != typeof(object); level = level.BaseType)
			{
				chain.Add(level);
			}
			chain.Reverse();

			List<FieldInfo> fields = new List<FieldInfo>();
			foreach (Type level in chain)
			{
				List<FieldInfo> declared = new List<FieldInfo>();
				foreach (var fieldInfo in level.GetFields(BindingFlags.Instance | BindingFlags.Public
															| BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
				{
					// The field's type is the declaration of intent — no opt-in attribute.
					if (!typeof(IDistributedField).IsAssignableFrom(fieldInfo.FieldType))
					{
						continue;
					}

					declared.Add(fieldInfo);
				}

				declared.Sort((f1, f2) => string.CompareOrdinal(f1.Name, f2.Name));
				fields.AddRange(declared);
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
		/// optimistic guarded updates from this fresh subscriber aren't rejected against pre-subscribe modifications.</param>
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
		/// optimistic guarded updates from this fresh subscriber aren't rejected against pre-subscribe modifications.</param>
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
		/// subscribed, causing its optimistic guarded updates to be rejected forever with no broadcast to correct them.</summary>
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
			FailTransactionsForEntity(entity);
			FailPendingConditionalActions(entity);
			PendingUpdateNotes.Remove(entity);

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
		/// <param name="note">The writer's notes on the update (a BSON array, one element per note), or null (see
		/// <see cref="IDistributedEntity.AddUpdateNote"/>).</param>
		/// <param name="noteSentMs">The writer's server-time stamp for <paramref name="note"/>.</param>
		/// <param name="noteIsOwn">True when this connection wrote the update.</param>
		public void HandleEntityUpdate(uint entityId, ArraySegment<byte> updateData, ushort seq, BsonValue? note = null, long noteSentMs = 0, bool noteIsOwn = false)
		{
			if (SuppressedEntities.ContainsKey(entityId)) return;

			IDistributedEntity entity = DistributedObjects.GetValueOrDefault(entityId);
			if (entity == null)
			{
				ImpunityLogger.LogWarning("Got property update for entity we don't know about: " + entityId);
				return;
			}

			if (note != null)
			{
				ApplyNotedUpdate(entity, updateData, seq, note, noteSentMs, noteIsOwn);
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
		/// <param name="sentServerMs">Server time the sender fired the event at (<see cref="EntityEvent.SentServerMs"/>).</param>
		/// <param name="isOwn">True when this connection fired the event (<see cref="EntityEvent.IsOwn"/>).</param>
		public void HandleEntityEvent(uint entityId, int eventType, BsonValue eventData, long sentServerMs, bool isOwn = false)
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
				entity.OnEventTriggered(new EntityEvent(eventType, eventData, sentServerMs, isOwn));
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
		/// <param name="sentServerMs">Server time the deleter sent the request at (<see cref="EntityDeletion.SentServerMs"/>).</param>
		/// <param name="isOwn">True when this connection's request deleted or replaced the entity.</param>
		public void HandleEntityDelete(uint entityId, BsonValue? deleteData, long sentServerMs = 0, bool isOwn = false)
		{
			if (SuppressedEntities.ContainsKey(entityId)) return;

			IDistributedEntity entity = DistributedObjects.GetValueOrDefault(entityId);
			if (entity == null)
			{
				ImpunityLogger.LogWarning("Got delete for entity we don't know about: " + entityId);
				return;
			}

			// Anything queued on this entity's lock can never be granted now.
			AbandonLockWaitsForEntity(entityId);

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
				entity.OnDeleted(new EntityDeletion(deleteData, sentServerMs, isOwn));
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
		/// <param name="allProperties">If true, serialize every field's full state regardless of dirty bits, pending
		/// changes included and default-valued fields left out (used for the initial state on every create path); if
		/// false (default), serialize only the currently dirty fields' deltas.</param>
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

			Stream stream = PropertyEncodingWriter.BaseStream;
			int startPos = (int)stream.Position;

			foreach (var fieldInfo in typeInfo.DistributedFields)
			{
				if (fieldInfo == null) continue;

				if (allProperties)
				{
					// Every field's full state, not just its delta: a create must carry the whole object even when
					// nothing is dirty (an instance re-created after a delete flushed its fields the first time).
					// A field with nothing to send is left out, exactly as an untouched field always was.
					long fieldStart = stream.Position;
					PropertyEncodingWriter.Write((byte)fieldInfo.FieldId);
					if (!(bool)fieldInfo.FullStateWriteMethod.Invoke(entity, WriteMethodArgs))
					{
						stream.Position = fieldStart;
					}
				}
				else if ((dirtyBits & fieldInfo.FieldBitmask) != 0)
				{
					PropertyEncodingWriter.Write((byte)fieldInfo.FieldId);
					fieldInfo.WriteMethod.Invoke(entity, WriteMethodArgs);
				}
			}

			entity.ClearDirty();

			if (stream.Position == startPos)
			{
				// Only reachable with allProperties: every field was at its default. Send nothing, as before.
				return null;
			}

			PropertyEncodingWriter.Write((byte)0);
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

		// Sends an entity's pending changes as an ordinary (unguarded) update. With forTransaction, or when a
		// transaction is waiting on the entity and this send is reliable, the update is sent reliably with a reply and
		// counted as in flight, so the transaction's body waits until the echo of this write has been applied (see
		// ClientEntityTransact.cs).
		private void SendEntityUpdates(IDistributedEntity entity, bool forTransaction = false)
		{
			if (Connection == null)
			{
				return;
			}

			ArraySegment<byte> updateDatabuffer = GetPropertyBytes(entity, out bool guaranteedSend);
			GameStateActionBase? conditionalAction = TakeConditionalAction(entity);
			BsonArray? note = TakeUpdateNotes(entity);

			if (updateDatabuffer.Array == null)
			{
				if (conditionalAction == null && note == null)
				{
					// Nothing changed and nothing to carry; don't burn a seq on an empty update.
					return;
				}

				// Only here to carry a conditional action or a note (e.g. a Set that turned out to be a no-op).
				updateDatabuffer = new ArraySegment<byte>(Array.Empty<byte>());
			}

			ImpunityCallback? onReply = null;
			if (forTransaction || ((guaranteedSend || conditionalAction != null || note != null) && HasTransactionPending(entity)))
			{
				guaranteedSend = true;
				onReply = TrackWrite(entity, null);
				TrackConditionalAction(entity, conditionalAction);

				// Copy out of the shared encode buffer: a reply is awaited, so this update may outlive the frame whose
				// sweep would reuse the buffer.
				byte[] copy = new byte[updateDatabuffer.Count];
				if (updateDatabuffer.Count > 0)
				{
					Array.Copy(updateDatabuffer.Array!, updateDatabuffer.Offset, copy, 0, updateDatabuffer.Count);
				}
				updateDatabuffer = new ArraySegment<byte>(copy);
			}

			entity.SendSeq++;
			Connection.UpdateEntity(entity.DistributedEntityId, updateDatabuffer, guaranteedSend, entity.SendSeq, onReply, conditionalAction, note);

		}

		/// <summary>Immediately flushes an entity's pending dirty fields as a guarded (optimistic-concurrency) update,
		/// bypassing the per-frame dirty sweep. The update carries the client's known per-field seqs; the server rejects the
		/// whole update (nothing applied) if any field changed since this client last saw it
		/// (<see cref="GuardedResult.Stale"/>), or if another connection holds the lock (<see cref="GuardedResult.Failed"/>
		/// with <see cref="ImpunityErrorCode.ActionBlockedByLock"/>).
		/// On success and on rejection the winning values are already applied locally by the time <paramref name="onResult"/>
		/// runs (see the ordering guarantee in docs/guides/DistributedEntities.md). Backs <see cref="IDistributedEntity.UpdateIfUnchanged"/>.
		/// The update counts as in flight until its reply arrives: a <c>Transact</c> on the same entity does not run its
		/// body before then. It is never held back itself, even while a transaction is active on the entity — it is sent
		/// at once, carrying whatever is dirty, as it always has been.</summary>
		/// <param name="entity">The entity whose pending dirty fields to flush as a guarded update.</param>
		/// <param name="onResult">Receives the outcome; the error is set only for <see cref="GuardedResult.Failed"/>.
		/// Delivered on a later <see cref="BaseGameConnection.Update"/>, never reentrantly.</param>
		/// <param name="onUpdatedAction">Optional database action to attach before flushing (see
		/// <see cref="AddConditionalAction"/>). Rides this update together with any actions already pending.</param>
		public void SendEntityUpdatesIfUnchanged(IDistributedEntity entity, ImpunityCallback<GuardedResult>? onResult, GameStateActionBase? onUpdatedAction = null)
		{
			ThrowIfInTransactBody(entity, "UpdateIfUnchanged");
			ImpunityCallback? onComplete = GuardedCallback(onResult);

			if (Connection == null)
			{
				// No connection to queue through, so both callbacks fire now, in the usual order.
				onComplete?.Invoke(new ImpunityErrorResponse(ImpunityErrorCode.ActionBadRequest, "Entity manager has no connection"));
				onUpdatedAction?.FailLocally(new ImpunityErrorResponse(ImpunityErrorCode.ActionBadRequest, "Entity manager has no connection"));
				return;
			}

			if (entity.Manager != this || entity.DistributedEntityId == 0 || !DistributedObjects.ContainsKey(entity.DistributedEntityId))
			{
				Connection.QueueLocalCallback(onComplete, new ImpunityErrorResponse(ImpunityErrorCode.ActionBadRequest, "Entity is not registered with this connection"));
				if (onUpdatedAction != null)
				{
					Connection.QueueLocalFailure(onUpdatedAction, new ImpunityErrorResponse(ImpunityErrorCode.ActionBadRequest, "Entity is not registered with this connection"));
				}
				return;
			}

			if (onUpdatedAction != null)
			{
				AddConditionalAction(entity, onUpdatedAction);
			}

			// Nothing dirty is trivially a success — unless a conditional action is waiting, which is sent on an empty
			// guarded update so it still gets the lock/staleness verdict (an empty seq blob checks nothing, but a
			// foreign lock still rejects it).
			if (entity.DirtyBits == 0 && !HasPendingUpdateExtras(entity))
			{
				Connection.QueueLocalCallback(onComplete, null);
				return;
			}

			// Counted as in flight until its reply arrives, so a Transact on this entity waits for it rather than
			// racing this connection's own write.
			SendGuardedNow(entity, TrackWrite(entity, onComplete));
		}

		/// <summary>Turns the raw reply of a guarded write into the <see cref="GuardedResult"/> the public API
		/// reports: no error is <see cref="GuardedResult.Applied"/>, <see cref="ImpunityErrorCode.ActionStaleData"/> is
		/// <see cref="GuardedResult.Stale"/> with no error, and anything else is <see cref="GuardedResult.Failed"/>
		/// with the error.</summary>
		private static ImpunityCallback? GuardedCallback(ImpunityCallback<GuardedResult>? onResult)
		{
			if (onResult == null)
			{
				return null;
			}

			return err =>
			{
				if (err == null)
				{
					onResult(null, GuardedResult.Applied);
				}
				else if (err.ErrorCode == ImpunityErrorCode.ActionStaleData)
				{
					onResult(null, GuardedResult.Stale);
				}
				else
				{
					onResult(err, GuardedResult.Failed);
				}
			};
		}

		// Builds and sends a guarded update from the entity's dirty fields and pending conditional actions. Callers
		// have checked the connection and registration, and that there is something to send.
		// extraGuard: fields the write does not change but depends on (a transaction's DependsOn), added to the blob.
		private void SendGuardedNow(IDistributedEntity entity, ImpunityCallback onComplete, ulong extraGuard = 0)
		{
			// The known-seqs blob covers the dirty fields plus any extra guard. Must be gathered before GetPropertyBytes
			// clears the dirty bits.
			byte[] seqBlob = BuildKnownSeqBlob(entity, entity.DirtyBits | extraGuard);

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

			// Every guarded send is a tracked write (UpdateIfUnchanged or a transaction's own update), so the actions riding
			// it count as in flight until they have reported too.
			GameStateActionBase? conditionalAction = TakeConditionalAction(entity);
			TrackConditionalAction(entity, conditionalAction);

			entity.SendSeq++;
			Connection!.UpdateEntityIfUnchanged(entity.DistributedEntityId, updateCopy, seqBlob, entity.SendSeq, onComplete, conditionalAction, TakeUpdateNotes(entity));
		}

		/// <summary>Builds the known-field-seq blob a guarded update or a guarded delete/replace carries:
		/// <c>[fieldId][seq lo][seq hi]...[0]</c> for each field in <paramref name="mask"/>, using this client's
		/// last-received seq per field (0 if never received). The server rejects the operation as stale if any listed
		/// field changed since.</summary>
		private byte[] BuildKnownSeqBlob(IDistributedEntity entity, ulong mask)
		{
			DistributedTypeInfo typeInfo = GetRegisteredTypeInfo(entity);

			int count = 0;
			foreach (var fieldInfo in typeInfo.DistributedFields)
			{
				if (fieldInfo != null && (mask & fieldInfo.FieldBitmask) != 0)
				{
					count++;
				}
			}

			byte[] seqBlob = new byte[count * 3 + 1];
			int pos = 0;
			foreach (var fieldInfo in typeInfo.DistributedFields)
			{
				if (fieldInfo == null || (mask & fieldInfo.FieldBitmask) == 0)
				{
					continue;
				}

				ushort known = entity.FieldRecvSeq != null ? entity.FieldRecvSeq[fieldInfo.FieldId] : (ushort)0;
				seqBlob[pos++] = fieldInfo.FieldId;
				seqBlob[pos++] = (byte)(known & 0xFF);
				seqBlob[pos++] = (byte)((known >> 8) & 0xFF);
			}
			seqBlob[pos] = 0;
			return seqBlob;
		}

		/// <summary>Builds the guard blob for a guarded delete or replace. Throws <see cref="ArgumentException"/> when the
		/// mask is empty (an unguarded delete must be asked for with <c>Delete</c>, never by accident) or names a bit
		/// that is not one of the entity type's distributed fields.</summary>
		private byte[] BuildGuardBlob(IDistributedEntity entity, ulong guardMask, string paramName)
		{
			if (guardMask == 0)
			{
				throw new ArgumentException("A guard must name at least one field (use a field's Guard property, combining several with |); use Delete for an unguarded delete", paramName);
			}

			ValidateFieldMask(entity, guardMask, paramName);
			return BuildKnownSeqBlob(entity, guardMask);
		}

		/// <summary>Throws <see cref="ArgumentException"/> when <paramref name="mask"/> names a bit that is not one of the
		/// entity type's distributed fields. Zero passes.</summary>
		internal void ValidateFieldMask(IDistributedEntity entity, ulong mask, string paramName)
		{
			ulong typeMask = 0;
			foreach (var fieldInfo in GetRegisteredTypeInfo(entity).DistributedFields)
			{
				if (fieldInfo != null)
				{
					typeMask |= fieldInfo.FieldBitmask;
				}
			}

			if ((mask & ~typeMask) != 0)
			{
				throw new ArgumentException("The mask names bits that are not distributed fields of " + entity.GetType().Name, paramName);
			}
		}

		/// <summary>Checks that <paramref name="entity"/> can be replaced by <paramref name="newObject"/> and returns the
		/// channel the replacement is created in. Only an object with a unique name, in a channel, can be replaced; the
		/// replacement takes that name (it is set on <paramref name="newObject"/> if it has none).</summary>
		/// <exception cref="InvalidOperationException">The entity is not an object with a unique name and a channel.</exception>
		/// <exception cref="ArgumentException"><paramref name="newObject"/> already has a different unique name.</exception>
		internal static IDistributedChannel ResolveReplaceTarget(IDistributedEntity entity, IDistributedObject newObject)
		{
			if (!(entity is IDistributedObject replaced) || replaced.UniqueName == null || replaced.Channel == null)
			{
				throw new InvalidOperationException("Only an object with a unique name, in a channel, can be replaced");
			}

			if (newObject.UniqueName == null)
			{
				newObject.UniqueName = replaced.UniqueName;
			}
			else if (newObject.UniqueName != replaced.UniqueName)
			{
				throw new ArgumentException("The replacement's unique name (" + newObject.UniqueName + ") differs from the replaced entity's ("
					+ replaced.UniqueName + ")", nameof(newObject));
			}

			return replaced.Channel;
		}

		/// <summary>Replaces an entity with <paramref name="newObject"/> only if the fields in <paramref name="guard"/> are
		/// unchanged since this client last received them. Backs <see cref="IDistributedEntity.ReplaceIfUnchanged{T}"/>;
		/// see there for the contract.</summary>
		/// <param name="entity">The registered object to replace. It must have a unique name.</param>
		/// <param name="guard">The fields of <paramref name="entity"/> the replace depends on. Must be non-empty.</param>
		/// <param name="newObject">The replacement, created in the entity's channel under its unique name.</param>
		/// <param name="replaceData">Optional cause payload relayed with the replaced entity's delete notification.</param>
		/// <param name="onResult"><see cref="GuardedResult.Applied"/> once the replacement is registered,
		/// <see cref="GuardedResult.Stale"/> when a guarded field changed, or <see cref="GuardedResult.Failed"/> with the
		/// error.</param>
		/// <param name="onCreatedAction">Optional conditional database action, run only if the replace goes through.</param>
		/// <exception cref="ArgumentException">The guard is empty or names fields of a different entity, or the
		/// replacement has a different unique name. Nothing is sent and <paramref name="onCreatedAction"/> is left to the
		/// caller.</exception>
		/// <exception cref="InvalidOperationException">The entity is not an object with a unique name and a channel.</exception>
		public void ReplaceEntityIfUnchanged<T>(IDistributedEntity entity, FieldGuard guard, T newObject, BsonValue? replaceData,
			ImpunityCallback<GuardedResult>? onResult, GameStateActionBase? onCreatedAction = null) where T : class, IDistributedObject
		{
			ThrowIfInTransactBody(entity, "ReplaceIfUnchanged");
			if (newObject == null)
			{
				throw new ArgumentNullException(nameof(newObject));
			}
			guard.MaskFor(entity, nameof(guard), true);
			ImpunityCallback? onError = GuardedCallback(onResult);

			if (Connection == null)
			{
				// No connection to queue through, so both callbacks fire now, in the usual order.
				onError?.Invoke(new ImpunityErrorResponse(ImpunityErrorCode.ActionBadRequest, "Entity manager has no connection"));
				onCreatedAction?.FailLocally(new ImpunityErrorResponse(ImpunityErrorCode.ActionBadRequest, "Entity manager has no connection"));
				return;
			}

			if (entity.Manager != this || entity.DistributedEntityId == 0 || !DistributedObjects.ContainsKey(entity.DistributedEntityId))
			{
				Connection.QueueLocalCallback(onError, new ImpunityErrorResponse(ImpunityErrorCode.ActionBadRequest, "Entity is not registered with this connection"));
				if (onCreatedAction != null)
				{
					Connection.QueueLocalFailure(onCreatedAction, new ImpunityErrorResponse(ImpunityErrorCode.ActionBadRequest, "Entity is not registered with this connection"));
				}
				return;
			}

			IDistributedChannel channel = ResolveReplaceTarget(entity, newObject);
			CreateObject(newObject, channel, true, (err, _) => onError?.Invoke(err), onCreatedAction, entity.DistributedEntityId, guard, replaceData);
		}

		/// <summary>Deletes an entity only if the fields in <paramref name="guard"/> are unchanged since this client
		/// last received them. Backs <see cref="IDistributedEntity.DeleteIfUnchanged"/>; see there for the contract.</summary>
		/// <param name="entity">The registered entity to delete.</param>
		/// <param name="guard">The fields the delete depends on: their <c>Guard</c>s combined with <c>|</c>. Must be non-empty.</param>
		/// <param name="deleteData">Optional payload relayed to subscribers with the delete notification.</param>
		/// <param name="onResult"><see cref="GuardedResult.Applied"/> if deleted, <see cref="GuardedResult.Stale"/>
		/// when a guarded field changed, or <see cref="GuardedResult.Failed"/> with the error (another connection's
		/// lock, entity gone).</param>
		/// <param name="onDeletedAction">Optional conditional database action, run only if this request deleted it.</param>
		/// <exception cref="ArgumentException">The guard is empty or names fields of a different entity. Nothing is sent
		/// and <paramref name="onDeletedAction"/> is left to the caller.</exception>
		public void DeleteEntityIfUnchanged(IDistributedEntity entity, FieldGuard guard, BsonValue? deleteData, ImpunityCallback<GuardedResult>? onResult, GameStateActionBase? onDeletedAction = null)
		{
			// The server answers a delete blocked by a foreign lock with "false" rather than an error (as a plain Delete
			// reports it); for a guarded write that is a failure like any other, with the same code a guarded update gets.
			ThrowIfInTransactBody(entity, "DeleteIfUnchanged");
			ulong guardMask = guard.MaskFor(entity, nameof(guard), true);
			ImpunityCallback? onError = GuardedCallback(onResult);
			uint entityId = entity.DistributedEntityId;
			ImpunityCallback<bool>? onComplete = onError == null ? null : (ImpunityCallback<bool>)((err, deleted) =>
				onError(err ?? (deleted ? null : new ImpunityErrorResponse(ImpunityErrorCode.ActionBlockedByLock, "Entity " + entityId + " is locked by another connection"))));

			if (Connection == null)
			{
				// No connection to queue through, so both callbacks fire now, in the usual order.
				onComplete?.Invoke(new ImpunityErrorResponse(ImpunityErrorCode.ActionBadRequest, "Entity manager has no connection"), false);
				onDeletedAction?.FailLocally(new ImpunityErrorResponse(ImpunityErrorCode.ActionBadRequest, "Entity manager has no connection"));
				return;
			}

			if (entity.Manager != this || entity.DistributedEntityId == 0 || !DistributedObjects.ContainsKey(entity.DistributedEntityId))
			{
				ImpunityCallback<bool>? notRegistered = onComplete;
				if (notRegistered != null)
				{
					Connection.QueueLocalCallback(err => notRegistered(err, false), new ImpunityErrorResponse(ImpunityErrorCode.ActionBadRequest, "Entity is not registered with this connection"));
				}
				if (onDeletedAction != null)
				{
					Connection.QueueLocalFailure(onDeletedAction, new ImpunityErrorResponse(ImpunityErrorCode.ActionBadRequest, "Entity is not registered with this connection"));
				}
				return;
			}

			byte[] guardBlob = BuildGuardBlob(entity, guardMask, nameof(guard));
			Connection.DoAction(new DeleteEntityAction(entity.DistributedEntityId, deleteData!, onComplete, onDeletedAction)
			{
				KnownFieldSeqs = new ArraySegment<byte>(guardBlob),
				SentServerMs = Connection.GetServerTime(),
			});
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
		/// <c>PersistAs</c> key. Fields the document does not mention are left unchanged — which is what lets a
		/// document written before a field existed still load. A field the document <em>does</em> carry is
		/// applied, including a stored null, so a round trip through the database preserves null for the field
		/// types that can hold one; a null stored against a non-nullable field is ignored by the field itself.
		/// Throws if the entity's type is not registered with this manager.</summary>
		/// <param name="entity">The entity to populate.</param>
		/// <param name="doc">The document holding persisted field values keyed by <c>PersistAs</c> name.</param>
		public void ApplyPersistedFieldsFromBson(IDistributedEntity entity, BsonDocument doc)
		{
			DistributedTypeInfo typeInfo = GetRegisteredTypeInfo(entity);

			foreach (var fieldInfo in typeInfo.DistributedFields)
			{
				if (fieldInfo == null) continue;

				if (fieldInfo.PersistedAs == null) continue;

				// BsonDocument's indexer answers BsonValue.Null for a key that is not there, so absence has to
				// be probed separately from a value that is genuinely null.
				if (!doc.ContainsKey(fieldInfo.PersistedAs)) continue;

				BsonValue fieldValue = doc[fieldInfo.PersistedAs];

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
