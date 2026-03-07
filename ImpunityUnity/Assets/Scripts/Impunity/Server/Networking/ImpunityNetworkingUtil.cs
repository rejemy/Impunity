using System;
using System.Text;
using System.Buffers.Binary;

using UltraLiteDB;

using System.IO;
using UnityEditor.VersionControl;

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

	public struct MessageHeaderStruct
	{
		public int Length;
		public ushort MessageType;
		public ushort MessageId;
		public ushort Flags;
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

		public static ArraySegment<byte> WriteMessage(ByteWriter writer, ushort messageId, ushort flags, ushort messageType, object message)
		{
			// Skip 4 bytes for length prefix
			writer.Position = 4;
			
			writer.Write(messageType);
			writer.Write(messageId);
			writer.Write(flags);
			writer.Write((ushort)0); // Padding

			if (message != null)
			{
				var mapper = ImpunityUtil.GetBsonMapper();
				mapper.SerializeToBytes(typeof(Message), message, writer);
			}

			int totalLength = writer.Position;

			if (totalLength >= ImpunityConstants.MaxMessageSize)
            {
				throw new Exception("Tried to send a message that's too large! Length: " + totalLength);
            }

			// Go back and write total length at start of buffer
			writer.Position = 0;
			writer.Write(totalLength);

			return new ArraySegment<byte>(writer.Buffer, 0, totalLength);
		}

		public static int ReadMessageHeader(ArraySegment<byte> messageBytes, out MessageHeaderStruct msg)
		{
			msg.Length = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(messageBytes.Array, messageBytes.Offset, 4));
			msg.MessageType = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(messageBytes.Array, messageBytes.Offset + 4, 2));
			msg.MessageId = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(messageBytes.Array, messageBytes.Offset + 6, 2));
			msg.Flags = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(messageBytes.Array, messageBytes.Offset + 8, 2));

			if (messageBytes.Count >= ImpunityConstants.MaxMessageSize)
			{
				throw new Exception("Received a message that's too large! Length: " + messageBytes.Count);
			}

			return 12;
		}

		public static int GetMessageLength(ArraySegment<byte> messageBytes)
		{
			return BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(messageBytes.Array, messageBytes.Offset, 4));
		}
	}
}
