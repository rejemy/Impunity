
using UltraLiteDB;


namespace Impunity.Networking
{
	/*
	public static class ServerMessageTypes
	{
		public const ushort REPLY = 1;
		public const ushort BROADCAST_MESSAGE = 2;
	}
	*/

	public static class ImpunityMessageFlags
	{
		public const ushort NO_REPLY = 1;
	}

	public class ServerAnnounceMessage
	{
		[BsonField("gn")]
		public string GameName;
	}



}
