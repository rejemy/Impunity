using System;
using System.Net;
using System.Net.Sockets;

namespace Impunity.Tests
{
	/// <summary>Allocates free ports for test servers. ImpunityServer binds both TCP and UDP on
	/// <c>ImpunityOptions.ServerPort</c> (no port-0 support), so a candidate port must be bindable
	/// on both protocols.</summary>
	public static class TestPorts
	{
		/// <summary>Returns a port that was momentarily bindable on both TCP and UDP. A race with
		/// another process is still possible between probing and the server's real bind, so server
		/// startup should retry with a fresh port on bind failure.</summary>
		public static ushort GetFreePort()
		{
			for (int attempt = 0; attempt < 10; attempt++)
			{
				var listener = new TcpListener(IPAddress.Loopback, 0);
				listener.Start();
				ushort port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
				listener.Stop();

				try
				{
					using (var udp = new UdpClient(port)) { }
				}
				catch (SocketException)
				{
					continue; // UDP side taken; try another TCP-assigned port
				}

				return port;
			}

			throw new InvalidOperationException("Could not find a port free on both TCP and UDP after 10 attempts");
		}
	}
}
