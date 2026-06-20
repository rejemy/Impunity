using System;
using System.Text;
using System.Security.Cryptography;

using UltraLiteDB;

using Impunity.GameState;
using System.IO;

namespace Impunity
{
	/// <summary>Pooled byte buffer with associated stream, writer, and reader. Dispose returns it to the pool instead of freeing it. Used to reduce GC pressure for serialization operations.</summary>
	public class TemporaryBuffer : IDisposable
	{
		/// <summary>True if this is a small buffer (<see cref="ImpunityConstants.MinMessageSize"/>), false for large (<see cref="ImpunityConstants.MaxMessageSize"/>).</summary>
		public readonly bool IsSmall;
		/// <summary>The underlying byte array.</summary>
		public readonly byte[] Bytes;
		/// <summary>MemoryStream wrapping <see cref="Bytes"/>.</summary>
		public readonly MemoryStream Stream;
		/// <summary>BinaryWriter for writing to the buffer.</summary>
		public readonly BinaryWriter Writer;
		/// <summary>BinaryReader for reading from the buffer.</summary>
		public readonly BinaryReader Reader;

		internal TemporaryBuffer? NextBuffer;

		/// <param name="small">True for a small buffer, false for a large buffer.</param>
		public TemporaryBuffer(bool small)
		{
			IsSmall = small;
			if (IsSmall)
			{
				Bytes = new byte[ImpunityConstants.MinMessageSize];
			}
			else
			{
				Bytes = new byte[ImpunityConstants.MaxMessageSize];
			}

			Stream = new MemoryStream(Bytes);
			Writer = new BinaryWriter(Stream);
			Reader = new BinaryReader(Stream);

			NextBuffer = null;
		}

		public void Dispose()
		{
			ImpunityUtil.ReturnBuffer(this);
		}
	}

	/// <summary>Shared utilities: buffer pooling, BSON mapper setup, hashing, and byte comparison helpers.</summary>
	public static class ImpunityUtil
	{
		private const int SERVER_ACTION_ID_OFFSET = 20000;
		private static BsonMapper? Mapper = null;
		
		private static TemporaryBuffer? SmallBufferPool;
		private static object SmallBufferLock = new object();

		private static TemporaryBuffer? LargeBufferPool;
		private static object LargeBufferLock = new object();

		/// <summary>Gets a small buffer from the pool, or allocates a new one if the pool is empty.</summary>
		public static TemporaryBuffer GetSmallBuffer()
		{
			TemporaryBuffer? buff = null;

			lock (SmallBufferLock)
			{
				if (SmallBufferPool != null)
				{
					buff = SmallBufferPool;
					SmallBufferPool = buff.NextBuffer;
				}
			}

			if (buff == null)
			{
				buff = new TemporaryBuffer(true);
			}
			else
			{
				buff.NextBuffer = null;
			}

			return buff;
		}

		/// <summary>Gets a large buffer from the pool, or allocates a new one if the pool is empty.</summary>
		public static TemporaryBuffer GetLargeBuffer()
		{
			TemporaryBuffer? buff = null;

			lock (LargeBufferLock)
			{
				if (LargeBufferPool != null)
				{
					buff = LargeBufferPool;
					LargeBufferPool = buff.NextBuffer;
				}
			}

			if (buff == null)
			{
				buff = new TemporaryBuffer(false);
			}
			else
			{
				buff.NextBuffer = null;
			}

			return buff;
		}

		/// <summary>Returns a buffer to its pool (small or large) for reuse. Resets the stream position.</summary>
		public static void ReturnBuffer(TemporaryBuffer b)
		{
			b.Stream.Position = 0;

			if (b.IsSmall)
			{
				lock (SmallBufferLock)
				{
					b.NextBuffer = SmallBufferPool;
					SmallBufferPool = b;
				}
			}
			else
			{
				lock (LargeBufferLock)
				{
					b.NextBuffer = LargeBufferPool;
					LargeBufferPool = b;
				}
			}

		}

		/// <summary>Returns the shared BsonMapper configured with all client and server action type registrations. Lazily initialized on first call.</summary>
		public static BsonMapper GetBsonMapper()
		{
			if (Mapper != null)
			{
				return Mapper;
			}

			Mapper = new BsonMapper();
			Mapper.IncludeFields = true;
			Mapper.IncludeFullType = false;

			foreach (ClientActionType actionTypeId in Enum.GetValues(typeof(ClientActionType)))
			{
				Type actionType = ClientActionFactory.GetActionClassType(actionTypeId);
				Mapper.RegisterTypeId(actionType, (int)actionTypeId);
			}

			foreach (ServerActionType actionTypeId in Enum.GetValues(typeof(ServerActionType)))
			{
				// Not a real server action type
				if (actionTypeId == ServerActionType.CLIENT_REPLY)
					continue;

				int intActionId = (int)actionTypeId + SERVER_ACTION_ID_OFFSET;
				Type actionType = ServerActionFactory.GetActionClassType(actionTypeId);
				Mapper.RegisterTypeId(actionType, intActionId);
			}

			return Mapper;
		}

		/// <summary>Checks if a byte array starts with the given header bytes.</summary>
		public static bool StartsWith(byte[] packet, byte[] header)
		{
			for (int i = 0; i < header.Length; i++)
			{
				if (i >= packet.Length || packet[i] != header[i])
				{
					return false;
				}
			}
			return true;
		}

		/// <summary>Computes an MD5 checksum of the BSON-serialized form of an object. Used for schema compatibility checks.</summary>
		public static string MakeDataChecksum(object dataObject)
		{
			BsonMapper mapper = GetBsonMapper();
			byte[] dataBytes = BsonSerializer.Serialize(mapper.SerializeObject(dataObject));

			StringBuilder sb = new StringBuilder();
			using (MD5 md5 = MD5.Create())
			{
				// Compute the hash of the given string
				byte[] hashValue = md5.ComputeHash(dataBytes);

				// Convert the byte array to string format
				foreach (byte b in hashValue)
				{
					sb.Append($"{b:X2}");
				}
			}

			return sb.ToString();
		}

		/// <summary>SHA-256 hashes a password string and returns it as base64. Returns null if input is null.</summary>
		public static string? HashPassword(string? pass)
		{
			if (pass == null)
			{
				return null;
			}

			using (SHA256 mySHA256 = SHA256.Create())
			{
				byte[] hash = mySHA256.ComputeHash(UTF8Encoding.UTF8.GetBytes(pass));
				return Convert.ToBase64String(hash);
			}
		}

		/// <summary>Lexicographic comparison of two byte array segments. Returns negative, zero, or positive.</summary>
		public static int CompareArraySegments(ArraySegment<byte> lh, ArraySegment<byte> rh)
		{
			if (lh.Array == null) return rh.Array == null ? 0 : -1;
			if (rh.Array == null) return 1;

			var result = 0;
			var i = 0;
			var stop = Math.Min(lh.Count, rh.Count);

			for (; 0 == result && i < stop; i++)
				result = lh[i].CompareTo(rh[i]);

			if (result != 0) return result < 0 ? -1 : 1;
			if (i == lh.Count) return i == rh.Count ? 0 : -1;
			return 1;
		}

		/// <summary>Parses a hex string key to its integer value.</summary>
		public static int KeyStringToInt(string key)
		{
			return int.Parse(key, System.Globalization.NumberStyles.HexNumber);
		}

		/// <summary>Converts an integer key to its uppercase hex string representation.</summary>
		public static string KeyIntToString(int key)
		{
			return key.ToString("X");
		}

	}
}
