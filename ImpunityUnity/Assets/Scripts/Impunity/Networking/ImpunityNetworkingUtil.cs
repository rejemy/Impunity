using System;
using System.Text;
using System.Buffers.Binary;

using UltraLiteDB;

namespace Impunity.Networking
{

	public struct MessageStruct
	{
		public int Length;
		public ushort MessageType;
		public ushort MessageId;
		public ushort Flags;
		public BsonDocument Body;
	}

	public static class ImpunityNetworkingUtil
	{
		private static BsonMapper Mapper = null;

		public static BsonMapper GetBsonMapper()
		{
			if (Mapper != null)
			{
				return Mapper;
			}

			Mapper = new BsonMapper();

			return Mapper;
		}


		// UDP broadcast packet format:
		//
		// UTF8encoded: "IMP{{ImpunityVersion}}_SRCH:{{GameTypeCode}}:{{BsonBody}}
		//

		public static ArraySegment<byte> WriteBroadcastPacket(byte[] destBuffer, byte[] header, BsonDocument message)
		{
			Buffer.BlockCopy(header, 0, destBuffer, 0, header.Length);
			int length = BsonWriter.SerializeTo(message, destBuffer, header.Length);
			return new ArraySegment<byte>(destBuffer, 0, length);
		}

		public static ArraySegment<byte> WriteBroadcastPacket(byte[] destBuffer, string header, BsonDocument message)
		{
			byte[] headerBytes = Encoding.UTF8.GetBytes(header);
			Buffer.BlockCopy(headerBytes, 0, destBuffer, 0, header.Length);
			int length = BsonWriter.SerializeTo(message, destBuffer, header.Length);
			return new ArraySegment<byte>(destBuffer, 0, length);
		}

		// Binary message format:
		//
		// All little-endian numbers:
		// 
		// 0  4 bytes: Length
		// 4  2 bytes: MessageType
		// 6  2 bytes: MessageId
		// 8  2 bytes: Flags
		// 10 2 bytes: Padding
		// 12 N bytes: Message
		//
		// Layout: LLLLTTIIFFPPMMMMMMMM...
		// 
		// 12 total header bytes

		public static ArraySegment<byte> WriteMessage(byte[] destBuffer, ushort messageId, ushort flags, ushort messageType, BsonDocument message)
		{
			BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(destBuffer, 4, 2), messageType);
			BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(destBuffer, 6, 2), messageId);
			BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(destBuffer, 8, 2), flags);
			BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(destBuffer, 10, 2), 0); // Padding

			int length = 12;
			if (message != null)
			{
				length = BsonWriter.SerializeTo(message, destBuffer, length);
			}

			BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(destBuffer, 0, 4), length);

			return new ArraySegment<byte>(destBuffer, 0, length);
		}

		public static void ReadMessage(byte[] messageBuffer, int length, out MessageStruct msg)
		{
			msg.Length = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(messageBuffer, 0, 4));
			msg.MessageType = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(messageBuffer, 4, 2));
			msg.MessageId = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(messageBuffer, 4, 2));
			msg.Flags = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(messageBuffer, 4, 2));

			if (length > 12)
			{
				msg.Body = null;
			}
			else
			{
				msg.Body = null;
			}
		}

		public static int GetMessageLength(byte[] messageBuffer)
		{
			return BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(messageBuffer, 0, 4));
		}

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
	}
}
