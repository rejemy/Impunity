
using UltraLiteDB;


namespace Impunity.Networking
{
	public static class ClientMessageTypes
	{
		public const ushort SET_SUMMARY = 1;
		public const ushort GET_SUMMARY = 2;
		public const ushort ENSURE_FORMAT = 3;
		public const ushort INSERT_DOCUMENT = 4;
		public const ushort UPDATE_DOCUMENT = 5;
		public const ushort UPSERT_DOCUMENT = 6;
		public const ushort FIND_DOCUMENT_BY_ID = 7;
		public const ushort DELETE_DOCUMENT = 8;
	}

	public static class ServerMessageTypes
	{
		public const ushort REPLY = 1;
	}

	public class ServerAnnounceMessage
	{
		[BsonField("gn")]
		public string GameName;
	}

	public class ServerReply
	{
		[BsonField("e")]
		public ImpunityError Error;
		[BsonField("r")]
		public BsonValue Result;
	}

	public class CollectionDocMessage
	{
		[BsonField("cid")]
		public int CollectionId;
		[BsonField("d")]
		public BsonDocument Doc;
	}

	public class CollectionIdMessage
	{
		[BsonField("cid")]
		public int CollectionId;
		[BsonField("id")]
		public BsonValue Id;
	}
}
