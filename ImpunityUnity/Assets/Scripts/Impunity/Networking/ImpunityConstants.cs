
namespace Impunity.Networking
{
	public static class ImpunityConstants
	{
		public const string ImpunityVersion = "1";
		public const ushort DefaultServerPort = 29654;
		public const ushort DefaultClientPort = 29655;
		public const int MaxMessageSize = 65000;
		public const string ServerSearchPacketHeader = "IMP" + ImpunityVersion + "_SRCH:";
		public const string ServerAnnouncePacketHeader = "IMP" + ImpunityVersion + "_ANNC:";
	}

	public static class ImpunityMessageFlags
	{
		public const ushort NO_REPLY = 1;
	}
}
