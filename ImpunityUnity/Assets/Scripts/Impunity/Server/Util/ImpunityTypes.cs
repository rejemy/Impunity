using System;
using System.Collections.Generic;
using System.Net;

using Impunity.GameState;
using UltraLiteDB;

namespace Impunity
{
	/// <summary>Callback for async operations that may fail. Null error indicates success.</summary>
	/// <param name="err">Error response, or null on success.</param>
	public delegate void ImpunityCallback(ImpunityErrorResponse? err);

	/// <summary>Callback for async operations that return a value. Null error indicates success.</summary>
	/// <typeparam name="TReturn">Type of the return value.</typeparam>
	/// <param name="err">Error response, or null on success.</param>
	/// <param name="returnValue">The result value on success.</param>
	public delegate void ImpunityCallback<TReturn>(ImpunityErrorResponse? err, TReturn returnValue);

	/// <summary>Protocol constants including version, ports, buffer sizes, and packet headers.</summary>
	public static class ImpunityConstants
	{
		public const string ImpunityVersion = "1";
		public const ushort DefaultServerHttpPort = 29653;
		public const ushort DefaultServerPort = 29654;
		public const ushort DefaultClientPort = 29655;
		public const int MinMessageSize = 4096;
		public const int MaxMessageSize = 65000;
		public const string ServerSearchPacketHeader = "SRCH_IMP" + ImpunityVersion + ":";
		public const string ServerAnnouncePacketHeader = "ANNC_IMP" + ImpunityVersion + ":";
		public const string ServerSessionDataPacketHeader = "DATA_IMP" + ImpunityVersion + ":";
		public const string ServerPingPacketHeader = "PING_IMP" + ImpunityVersion + ":";
		public const string ServerPongPacketHeader = "PONG_IMP" + ImpunityVersion + ":";
	}

	/// <summary>Configuration options for an Impunity server instance.</summary>
	public class ImpunityOptions
	{
		/// <summary>Password required for database access. Null for no password.</summary>
		public string? DBPassword = null;
		/// <summary>Whether clients can trigger database schema upgrades remotely.</summary>
		public bool RemoteUpgradeAllowed = false;
		/// <summary>Whether the server broadcasts its presence via UDP for LAN discovery.</summary>
		public bool LANDiscoverable = false;
		/// <summary>Maximum number of simultaneous client connections.</summary>
		public int MaxConnections = 8;
		/// <summary>TCP port the server listens on.</summary>
		public ushort ServerPort = ImpunityConstants.DefaultServerPort;
		/// <summary>UDP port used for client-side discovery broadcasts.</summary>
		public ushort ClientPort = ImpunityConstants.DefaultClientPort;
		/// <summary>Short identifier for the game type, used in discovery packets.</summary>
		public string GameTypeCode = "IMP";
		/// <summary>Timeout in milliseconds for client actions awaiting server response.</summary>
		public int ActionTimeoutMillis = 5 * 1000;
		/// <summary>
		/// Idle timeout, in milliseconds, for a data migration. While a migration is being offered (a higher-version
		/// client is deciding whether to migrate) or actively running, the world is reserved for the owning connection.
		/// If no migration activity happens within this window the offer/migration is abandoned and the world is
		/// released (an in-progress migration is rolled back from its backup). Reset on every migration operation.
		/// </summary>
		public int MigrationIdleTimeoutMillis = 5 * 60 * 1000;
		/// <summary>
		/// How long, in milliseconds, a channel may have zero subscribers before it is reaped from live memory.
		/// Persisted channels keep their database data and reload on the next subscribe; ephemeral channels are
		/// gone for good. 0 or negative disables idle-channel cleanup.
		/// </summary>
		public int IdleChannelTimeoutMillis = 5 * 60 * 1000;
	}

	/// <summary>Flags describing how a game state entity instance behaves.</summary>
	[Flags]
	public enum ImpunityInstanceFlags : byte
	{
		None = 0,
		/// <summary>Entity state is owned and updated by the client rather than the server.</summary>
		ClientAuthoritative = 1,
		/// <summary>Entity state is persisted to the database between sessions.</summary>
		Persisted = 2,
		/// <summary>Entity is automatically deleted when the creating client disconnects.</summary>
		DeleteOnDisconnect = 4
	}

	public enum ImpunityInternalCollectionIds
	{
		Entities = 1
	}

	public enum ImpunityLogLevel
	{
		TRACE = 1,
		DEBUG = 2,
		INFO = 3,
		WARN = 4,
		ERROR = 5,
		CRITICAL = 6
	}

	public enum ImpunityErrorCode
	{
		UnknownError = 0,
		InternalServerError = 1,

		ClientUnableToConnectError = 1000,
		ClientConnectionBrokenError = 1001,
		TimeoutError = 1002,

		ServerUnavailable = 2000, // New connections to the server are temporarily paused
		ServerPasswordIncorrect = 2001, // Attempt to connect to password protected server with the wrong password
		ServerVersionIncompatible = 2002, // Client is not the same version as the server
		ServerMigrationInProgress = 2003, // A data migration is being offered or run on this world; other connections are refused

		ActionInvalidParameter = 3000,
		ActionBadRequest = 3001,
		ActionCompoundFailure = 3002,
		ActionUniqueNameExists = 3003,
		ActionNotFound = 3004,
		ActionBlockedByLock = 3005,
	}

	/// <summary>Persisted metadata about a game world, stored in the database.</summary>
	public class GameMetadata
	{
		[BsonId]
		public string Id = null!;

		[BsonField("Version")]
		public int Version;

		[BsonField("DataFormatChecksum")]
		public string DataFormatChecksum = null!;

		[BsonField("Collections")]
		public GameStateCollection[] Collections = null!;

		[BsonField("EntityTypes")]
		public GameStateEntityTypeDef[]? EntityTypes;
	}

	/// <summary>
	/// On-disk marker written when a data migration actually begins (the database has been snapshotted). Its presence
	/// after a restart means a migration was interrupted: if <see cref="ToVersion"/> equals the world's persisted
	/// <see cref="GameMetadata.Version"/> the commit had already landed and only cleanup is needed, otherwise the
	/// pre-migration snapshot must be restored. An offered-but-not-begun migration writes no marker.
	/// </summary>
	public class MigrationMarker
	{
		/// <summary>The world's schema version before the migration (the snapshot's version).</summary>
		[BsonField("from")]
		public int FromVersion;

		/// <summary>The schema version the migration is upgrading the world to.</summary>
		[BsonField("to")]
		public int ToVersion;

		/// <summary>Connection id of the client that owns the migration.</summary>
		[BsonField("owner")]
		public string Owner = null!;

		/// <summary>Server Unix-millisecond timestamp when the migration began.</summary>
		[BsonField("at")]
		public long StartedAtMillis;

		public MigrationMarker() { }

		public MigrationMarker(int fromVersion, int toVersion, string owner, long startedAtMillis)
		{
			FromVersion = fromVersion;
			ToVersion = toVersion;
			Owner = owner;
			StartedAtMillis = startedAtMillis;
		}
	}

	/// <summary>Exception with an associated <see cref="ImpunityErrorCode"/>, used for server-side errors that should be reported to the client.</summary>
	public class ImpunityServerException : Exception
	{
		/// <summary>The error code identifying the type of failure.</summary>
		public ImpunityErrorCode ErrorCode { get; private set; }

		public ImpunityServerException(ImpunityErrorCode errorCode, string message) : base(message)
		{
			ErrorCode = errorCode;
		}
	}

	/// <summary>A fatal server exception that should terminate the connection.</summary>
	public class ImpunityServerFatalException : ImpunityServerException
	{
		public ImpunityServerFatalException(ImpunityErrorCode errorCode, string message) : base(errorCode, message) { }
	}


	/// <summary>Serializable error response sent from server to client. Contains an error code, message, and optional stack trace.</summary>
	public class ImpunityErrorResponse
	{
		// Serialized form of ErrorCode. Must be public so the BsonMapper includes it on the wire; a protected/internal
		// member is skipped, which would drop the error code from remote replies (leaving only the message).
		[BsonField("err")]
		public int ErrorInt { get; private set; }

		/// <summary>The error code identifying the type of failure.</summary>
		[BsonIgnore]
		public ImpunityErrorCode ErrorCode
		{
			get { return (ImpunityErrorCode)ErrorInt; }
			set { ErrorInt = (int)value; }
		}

		/// <summary>Human-readable error message.</summary>
		[BsonField("msg")]
		public string Message { get; private set; } = default!;

		/// <summary>Server-side stack trace for debugging. May be null.</summary>
		[BsonField("stk")]
		public string? Stacktrace { get; private set; }

		public ImpunityErrorResponse() { }

		/// <summary>Creates an error response from an exception. Uses <see cref="ImpunityErrorCode.InternalServerError"/> unless the exception is an <see cref="ImpunityServerException"/>.</summary>
		public ImpunityErrorResponse(Exception e)
		{
			if (e is ImpunityServerException ise)
			{
				ErrorCode = ise.ErrorCode;
			}
			else
			{
				ErrorCode = ImpunityErrorCode.InternalServerError;
			}
			Message = e.Message;
			Stacktrace = e.StackTrace;
		}

		public ImpunityErrorResponse(ImpunityServerException e)
		{
			ErrorCode = e.ErrorCode;
			Message = e.Message;
			Stacktrace = e.StackTrace;
		}

		public ImpunityErrorResponse(ImpunityErrorCode code, Exception e)
		{
			ErrorCode = code;
			Message = e.Message;
			Stacktrace = e.StackTrace;
		}

		public ImpunityErrorResponse(ImpunityErrorCode code, string message, Exception e)
		{
			ErrorCode = code;
			Message = message;
			Stacktrace = e.StackTrace;
		}

		public ImpunityErrorResponse(ImpunityErrorCode code, string message)
		{
			ErrorCode = code;
			Message = message;
		}

	}

	/// <summary>Client-side exception wrapping an <see cref="ImpunityErrorResponse"/> received from the server.</summary>
	public class ImpuntyErrorResponseException : Exception
	{
		/// <summary>The error code from the server response.</summary>
		public ImpunityErrorCode ErrorId { get; private set; }
		/// <summary>The stack trace from the server, for debugging.</summary>
		public string? ServerStacktrace { get; private set; }

		public ImpuntyErrorResponseException(ImpunityErrorResponse err) : base(err.Message)
		{
			ErrorId = err.ErrorCode;
			ServerStacktrace = err.Stacktrace;
		}

		public override string ToString()
		{
			return "Error " + ErrorId + ": " + Message + "\n" + ServerStacktrace;
		}
	}

	/// <summary>Internal listener for game state changes, used by the network layer to broadcast updates.</summary>
	internal interface IGameStateListener
	{
		/// <summary>Called when the game's metadata (schema, version) changes.</summary>
		void OnGameMetadataChanged(GameStateServer game);
		/// <summary>Called when the game's summary (player count, etc.) changes.</summary>
		void OnGameSummaryChanged(GameStateServer game);
	}

	/// <summary>Defines the schema of a game's state: its version, collections, and entity types. Used at connection time to verify client/server compatibility.</summary>
	public class GameStateFormat
	{
		/// <summary>Schema version number. Incremented when the format changes.</summary>
		public int Version;

		/// <summary>Named data collections (channels) clients can subscribe to.</summary>
		public GameStateCollection[] Collections;

		/// <summary>CLR types of all distributed entity classes, used to build the entity type registry.</summary>
		public Type[] EntityTypes;

		/// <summary>Creates a new game state format definition.</summary>
		public GameStateFormat(int version, GameStateCollection[] collections, Type[] entityTypes)
		{
			Version = version;

			Collections = collections;
			EntityTypes = entityTypes;
		}
	}

	/// <summary>Serializable version of <see cref="GameStateFormat"/> exchanged between client and server during connection handshake. Includes a checksum for compatibility verification.</summary>
	public class GameStateFormatData
	{
		[BsonField("v")]
		public int Version;

		[BsonField("dc")]
		public string DataChecksum = null!;

		[BsonField("cs")]
		public GameStateCollection[] Collections = null!;

		[BsonField("es")]
		public GameStateEntityTypeDef[]? EntityTypes;

		public GameStateFormatData()
		{ }


		public GameStateFormatData(GameStateFormat format, GameStateEntityTypeDef[]? entityTypes)
		{
			Version = format.Version;

			Collections = format.Collections;
			if (Collections != null && Collections.Length > 0)
			{
				Array.Sort(Collections,
					(c1, c2) =>
					{
						return c1.Index - c2.Index;
					}
				);

				if (Collections[0].Index < 10)
				{
					throw new Exception("Can't use 0 - 9 as a collection index");
				}
			}

			// Entity types should be pre-sorted from ClientEntityManager
			EntityTypes = entityTypes;

			DataChecksum = ImpunityUtil.MakeDataChecksum(this);
		}

	}

	/// <summary>A named data collection (channel) that clients can subscribe to for state updates.</summary>
	public class GameStateCollection
	{
		[BsonField("i")]
		public int Index;

		[BsonField("n")]
		public string Name = null!;

	}

	/// <summary>Wire type identifier for distributed entity property values. Used in serialization and schema definitions.</summary>
	public enum GameStateEntityPropertyValueType : byte
	{
		Boolean = 1,

		Int8 = 2,
		UInt8 = 3,
		Int16 = 4,
		UInt16 = 5,
		Int32 = 6,
		UInt32 = 7,
		Int64 = 8,
		UInt64 = 9,

		Float = 10,
		Double = 11,
		Decimal = 12,

		Char = 13,
		String = 14,
		Blob = 15,

		DateTime = 16,
		DateTimeOffset = 17,
		TimeSpan = 18,
		Guid = 19,

		CustomSmall = 100,
		CustomSmallNullable = 101,
		Custom = 102,
		CustomNullable = 103
	}

	/// <summary>Container type for a distributed entity field (scalar, array, queue, or dictionary).</summary>
	public enum GameStateEntityFieldType : byte
	{
		Value = 1,
		Array = 2,
		Queue = 3,
		IntDictionary = 4,
		StringDictionary = 5
	}

	/// <summary>Type of update operation for a distributed collection field.</summary>
	public enum DistributedCollectionUpdateType : byte
	{
		None = 0,
		Set = 1,
		Update = 2
	}

	/// <summary>Schema definition for a single property on a distributed entity type.</summary>
	public class GameStateEntityPropertyDef
	{
		[BsonField("id")]
		public int Index;

		[BsonField("n")]
		public string Name = null!;

		[BsonField("ft")]
		public byte FieldType = (byte)GameStateEntityFieldType.Value;

		[BsonField("v")]
		public byte PropValueType;

		[BsonField("pa")]
		public string? PersistedAs;

		[BsonField("tm")]
		public bool IsTemporal;

		public GameStateEntityPropertyDef()
		{

		}

		public GameStateEntityPropertyDef(int index, string name, byte fieldType, byte propValueType, string? persistedAs, bool isTemporal)
		{
			Index = index;
			Name = name;
			FieldType = fieldType;
			PropValueType = propValueType;
			PersistedAs = persistedAs;
			IsTemporal = isTemporal;
		}
	}

	/// <summary>Schema definition for a distributed entity type, including its properties.</summary>
	public class GameStateEntityTypeDef
	{
		[BsonField("id")]
		public int Index;

		[BsonField("n")]
		public string Name = null!;

		[BsonField("pa")]
		public string? PersistedAs;

		[BsonField("ps")]
		public GameStateEntityPropertyDef[] Properties = null!;
	}

	/// <summary>Info about a single game world on a standalone server, returned by the HTTP discovery API.</summary>
	public class StandaloneServerWorldInfo
	{
		public string WorldId { get; set; } = null!;
		public string WorldName { get; set; } = null!;
		public bool PasswordProtected { get; set; }
		public int CurrentPlayers { get; set; }
		public int MaxPlayers { get; set; }
		public string? GameSummary { get; set; } = null!;
		public int GameVersion { get; set; }
		public string DataFormatChecksum { get; set; } = null!;
	}

	/// <summary>Top-level response from the standalone server HTTP discovery API, listing all available worlds.</summary>
	public class StandaloneServerWorldsInfo
	{
		public string ImpunityVersion { get; set; } = null!;
		public string GameType { get; set; } = null!;
		public int? TCPPort { get; set; }
		public List<StandaloneServerWorldInfo> Worlds { get; set; } = null!;
	}

	/// <summary>Client-side representation of a discovered server. Populated by either LAN UDP discovery or HTTP discovery.</summary>
	public class ServerInfo
	{
		/// <summary>Display name of the game world.</summary>
		public string WorldName = null!;

		/// <summary>Hostname for remote servers (mutually exclusive with <see cref="Address"/>).</summary>
		public string? Hostname;
		/// <summary>TCP port for remote servers (used with <see cref="Hostname"/>).</summary>
		public int Port;
		/// <summary>Direct IP endpoint for LAN servers (mutually exclusive with <see cref="Hostname"/>/<see cref="Port"/>).</summary>
		public IPEndPoint? Address;

		/// <summary>Unique identifier for the game world on the server.</summary>
		public string GameId = null!;
		/// <summary>Whether the server requires a password to connect.</summary>
		public bool PasswordProtected;

		/// <summary>Number of players currently connected.</summary>
		public int CurrentPlayers { get; set; }
		/// <summary>Maximum players allowed.</summary>
		public int MaxPlayers { get; set; }

		/// <summary>The game state schema version the server is running.</summary>
		public int GameStateFormatVersion;
		/// <summary>Checksum of the game state schema, for client/server compatibility verification.</summary>
		public string GameStateFormatChecksum = null!;
		/// <summary>Custom game summary data (e.g. map name, game mode) as a BSON document.</summary>
		public BsonDocument? GameSummary;

	}
}
