using System;
using System.Text;
using System.Buffers.Binary;

using UltraLiteDB;

using System.IO;

namespace Impunity.Networking
{

	public interface INetworkable
	{
		void WriteTo(BinaryWriter w);
		void ReadFrom(BinaryReader r);
	}


	public static class ImpunityMessageFlags
	{
		public const ushort NO_REPLY = 1;
	}

	public class ServerAnnounceMessage
	{
		[BsonField("gn")]
		public string GameName;
	}

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

		// UDP broadcast packet format:
		//
		// UTF8encoded: "IMP{{ImpunityVersion}}_SRCH:{{GameTypeCode}}:{{BsonBody}}
		//

		public static ArraySegment<byte> MakeBroadcastPacket(byte[] destBuffer, string header, BsonDocument body)
		{
			byte[] headerBytes = Encoding.UTF8.GetBytes(header);
			Buffer.BlockCopy(headerBytes, 0, destBuffer, 0, headerBytes.Length);
			int pos = headerBytes.Length;
			if (body != null)
			{
				pos = BsonWriter.SerializeTo(body, destBuffer, headerBytes.Length);
			}
			return new ArraySegment<byte>(destBuffer, 0, pos);
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

			if (length >= ImpunityConstants.MaxMessageSize)
            {
				throw new Exception("Tried to send a message that's too large! Length: " + length);
            }

			BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(destBuffer, 0, 4), length);

			return new ArraySegment<byte>(destBuffer, 0, length);
		}

		public static void ReadMessage(ArraySegment<byte> messageBytes, out MessageStruct msg)
		{
			msg.Length = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(messageBytes.Array, messageBytes.Offset, 4));
			msg.MessageType = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(messageBytes.Array, messageBytes.Offset + 4, 2));
			msg.MessageId = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(messageBytes.Array, messageBytes.Offset + 6, 2));
			msg.Flags = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(messageBytes.Array, messageBytes.Offset + 8, 2));

			if (messageBytes.Count >= ImpunityConstants.MaxMessageSize)
			{
				throw new Exception("Received a message that's too large! Length: " + messageBytes.Count);
			}

			if (messageBytes.Count > 12)
			{
				msg.Body = BsonReader.Deserialize(new ArraySegment<byte>(messageBytes.Array, messageBytes.Offset + 12, msg.Length));
			}
			else
			{
				msg.Body = null;
			}
		}

		public static int GetMessageLength(ArraySegment<byte> messageBytes)
		{
			return BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(messageBytes.Array, messageBytes.Offset, 4));
		}
	}
}
