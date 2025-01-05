#nullable enable

using Impunity;
using Impunity.Networking;

namespace Impunity.StandaloneServer;


public class ConnectionService
{
	private readonly WorldService Worlds;
	public readonly ImpunityServer NetworkServer;

	public int Port { get
	{
		return NetworkServer.Options.ServerPort;
	}}

	public ConnectionService(WorldService worldService, ImpunityOptions options)
	{
		Worlds = worldService;
		NetworkServer = new ImpunityServer(Worlds.GetGameStateServers(), options);
	}

	public void Start()
	{
		NetworkServer.Start();
	}

	public void Stop()
	{
		NetworkServer.Dispose();
	}
}