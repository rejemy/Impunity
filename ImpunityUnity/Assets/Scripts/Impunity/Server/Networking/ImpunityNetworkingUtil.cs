using System;
using System.Text;
using System.Buffers.Binary;

using UltraLiteDB;

using System.IO;

namespace Impunity.Networking
{

	/// <summary>Interface for types that can serialize/deserialize themselves to a binary stream.</summary>
	public interface INetworkable
	{
		/// <summary>Writes this object's data to the writer.</summary>
		void WriteTo(BinaryWriter w);
		/// <summary>Reads this object's data from the reader, populating fields.</summary>
		void ReadFrom(BinaryReader r);
	}

	/// <summary>Bit flags for the message header Flags field.</summary>
	public static class ImpunityMessageFlags
	{
		/// <summary>Indicates the sender does not expect a reply to this message.</summary>
		public const ushort NO_REPLY = 1;
	}

	/// <summary>BSON-serialized body of a UDP server announce broadcast packet.</summary>
	public class ServerAnnounceMessage
	{
		[BsonField("gn")]
		public string GameName = null!;
	}

	/// <summary>Parsed binary message header. See <see cref="ImpunityNetworkingUtil"/> for wire format.</summary>
	public struct MessageHeaderStruct
	{
		/// <summary>Total message length in bytes (including header).</summary>
		public int Length;
		/// <summary>Action type identifier.</summary>
		public ushort MessageType;
		/// <summary>Sequence ID for request/reply correlation.</summary>
		public ushort MessageId;
		/// <summary>Bit flags (see <see cref="ImpunityMessageFlags"/>).</summary>
		public ushort Flags;
	}

	/// <summary>Utilities for encoding/decoding the binary wire protocol. Messages use a 12-byte header: 4-byte length, 2-byte type, 2-byte id, 2-byte flags, 2-byte padding, followed by a BSON body.</summary>
	public static class ImpunityNetworkingUtil
	{

		// UDP broadcast packet format:
		//
		// UTF8encoded: "IMP{{ImpunityVersion}}_SRCH:{{GameTypeCode}}:{{BsonBody}}
		//

		/// <summary>Writes a UDP broadcast packet (UTF-8 header + optional BSON body) into the destination buffer.</summary>
		/// <returns>ArraySegment covering the written portion of <paramref name="destBuffer"/>.</returns>
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

		/// <summary>Serializes a message (header + BSON body) into the writer's buffer. The length prefix is written last. The writer's buffer is reused across calls.</summary>
		/// <returns>ArraySegment covering the complete serialized message.</returns>
		/// <exception cref="Exception">Thrown if the serialized message exceeds <see cref="ImpunityConstants.MaxMessageSize"/>.</exception>
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
				mapper.SerializeToBytes(message.GetType(), message, writer);
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

		/// <summary>Parses the 12-byte message header from a byte segment. Validates the length field.</summary>
		/// <returns>The header size in bytes (always 12).</returns>
		public static int ReadMessageHeader(ArraySegment<byte> messageBytes, out MessageHeaderStruct msg)
		{
			msg.Length = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(messageBytes.Array, messageBytes.Offset, 4));
			msg.MessageType = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(messageBytes.Array, messageBytes.Offset + 4, 2));
			msg.MessageId = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(messageBytes.Array, messageBytes.Offset + 6, 2));
			msg.Flags = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(messageBytes.Array, messageBytes.Offset + 8, 2));

			if (msg.Length <= 0 || msg.Length >= ImpunityConstants.MaxMessageSize)
			{
				throw new Exception("Received a message with invalid length: " + msg.Length);
			}

			return 12;
		}

		/// <summary>Reads just the 4-byte little-endian length prefix from a message buffer. Used to determine if a complete message has been received.</summary>
		public static int GetMessageLength(ArraySegment<byte> messageBytes)
		{
			return BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(messageBytes.Array, messageBytes.Offset, 4));
		}
	}
}
