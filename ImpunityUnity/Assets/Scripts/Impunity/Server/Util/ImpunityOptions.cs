using Impunity.Networking;

namespace Impunity
{

	public class ImpunityOptions
	{
		public bool LANDiscoverable = false;
		public ushort ServerPort = ImpunityConstants.DefaultServerPort;
		public ushort ClientPort = ImpunityConstants.DefaultClientPort;
		public string GameTypeCode = "IMP";
	}

}