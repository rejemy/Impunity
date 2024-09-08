#nullable enable

using Impunity;
using Impunity.Networking;

namespace Impunity.StandaloneServer;


public class TCPConnectionService
{
	private readonly WorldService Worlds;
	private readonly ImpunityServer TCPServer;

	public TCPConnectionService(WorldService worldService, ImpunityOptions options)
	{
		Worlds = worldService;
		TCPServer = ImpunityServer.MakeTCPServer(Worlds.GetGameStateServers(), options);
	}

	public void Start()
	{
		TCPServer.Start();
	}

	public void Stop()
	{
		TCPServer.Dispose();
	}
}