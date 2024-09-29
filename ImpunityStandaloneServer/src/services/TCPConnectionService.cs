#nullable enable

using Impunity;
using Impunity.Networking;

namespace Impunity.StandaloneServer;


public class TCPConnectionService
{
	private readonly WorldService Worlds;
	private readonly ImpunityServer TCPServer;

	public int Port { get
	{
		return TCPServer.Options.ServerPort;
	}}

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