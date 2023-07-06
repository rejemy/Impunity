using System;
using System.Text;
using System.Buffers.Binary;

using UltraLiteDB;

using Impunity.GameState;

namespace Impunity.Networking
{

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
		private static BsonMapper Mapper = null;
		private const int SERVER_ACTION_ID_OFFSET = 20000;

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


		// UDP broadcast packet format:
		//
		// UTF8encoded: "IMP{{ImpunityVersion}}_SRCH:{{GameTypeCode}}:{{BsonBody}}
		//

		public static ArraySegment<byte> MakeBroadcastPacket(byte[] destBuffer, string header, byte[] summaryBytes, int summaryBytesLength)
		{
			byte[] headerBytes = Encoding.UTF8.GetBytes(header);
			Buffer.BlockCopy(headerBytes, 0, destBuffer, 0, headerBytes.Length);
			if (summaryBytes != null)
			{
				Buffer.BlockCopy(summaryBytes, 0, destBuffer, headerBytes.Length, summaryBytesLength);
			}
			return new ArraySegment<byte>(destBuffer, 0, headerBytes.Length + summaryBytesLength);
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
