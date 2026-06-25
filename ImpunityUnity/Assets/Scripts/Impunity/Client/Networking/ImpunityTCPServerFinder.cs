#if !UNITY_WEBGL

using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

using UltraLiteDB;

namespace Impunity.Networking
{

	/// <summary>Discovers Impunity servers on the LAN via UDP broadcast. Sends search packets and listens for announce responses. Call <see cref="Update"/> on the main thread to receive callbacks.</summary>
	public class ImpunityTCPServerFinder : IDisposable
	{
		private static ImpunityTCPServerFinder? Instance;

		ImpunityOptions Options;
		Thread? UDPListenerThread;
		UdpClient? FinderUdpSocket;
		bool Running;

		byte[] SearchPacket;

		Action<ServerInfo> OnServerFoundCallback;

		ConcurrentDictionary<IPEndPoint, ServerInfo> ServersFound;
		ConcurrentQueue<ServerInfo> NotificationQueue;

		byte[] ServerAnnounceHeader;

		/// <summary>Creates a server finder. The <paramref name="onServerFound"/> callback is invoked on the main thread via <see cref="Update"/>.</summary>
		public ImpunityTCPServerFinder(ImpunityOptions options, Action<ServerInfo> onServerFound)
		{
			if (Instance != null)
			{
				Instance.Dispose();
				Instance = null;
			}

			Options = options;
			OnServerFoundCallback = onServerFound;

			ServerAnnounceHeader = Encoding.UTF8.GetBytes(ImpunityConstants.ServerAnnouncePacketHeader + Options.GameTypeCode + ":");

			ServersFound = new ConcurrentDictionary<IPEndPoint, ServerInfo>();
			NotificationQueue = new ConcurrentQueue<ServerInfo>();

			SearchPacket = Encoding.UTF8.GetBytes(ImpunityConstants.ServerSearchPacketHeader + Options.GameTypeCode + ":");

			Instance = this;
		}

		/// <summary>Starts the UDP listener thread and sends the initial search broadcast.</summary>
		public void Start()
		{
			Running = true;
			UDPListenerThread = new Thread(new ThreadStart(UDPListener));
			UDPListenerThread.IsBackground = true;
			UDPListenerThread.Name = "Server Finder UDP";
			UDPListenerThread.Start();
		}

		public static void Cleanup()
		{
			if (Instance != null)
			{
				Instance.Dispose();
				Instance = null;
			}
		}

		public void Dispose()
		{
			Running = false;
			if (FinderUdpSocket != null)
			{
				FinderUdpSocket.Close();
				FinderUdpSocket = null;
			}
		}

		/// <summary>Drains the notification queue and invokes the onServerFound callback for each newly discovered server. Must be called on the main thread (e.g., from Unity Update).</summary>
		public void Update()
		{
			if (!Running)
			{
				return;
			}

			ServerInfo info;
			while (NotificationQueue.TryDequeue(out info))
			{
				try
				{
					OnServerFoundCallback.Invoke(info);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception processing onServerFound callback", e);
				}
			}
		}

		private void UDPListener()
		{
			FinderUdpSocket = null;

			try
			{
				FinderUdpSocket = new UdpClient(Options.ClientPort);
				FinderUdpSocket.EnableBroadcast = true;

				SendServerSearch();

				while (Running && !ImpunityLifecycle.ShuttingDown)
				{
					if (!FinderUdpSocket.Client.Poll(1_000_000, SelectMode.SelectRead))
					{
						continue;
					}

					if (FinderUdpSocket == null)
					{
						break;
					}

					IPEndPoint groupEP = null!;
					byte[] packet = FinderUdpSocket.Receive(ref groupEP);
					if (ImpunityUtil.StartsWith(packet, ServerAnnounceHeader))
					{
						ImpunityLogger.LogDebug("Got server announce");
						OnServerAnnounce(packet, ref groupEP);
					}
					// Some other packet, ignore!
				}
			}
			catch (SocketException e)
			{
				if (!Running)
				{
					return;
				}

				ImpunityLogger.LogError("Socket error", e);
			}
			finally
			{
				if (FinderUdpSocket != null)
				{
					FinderUdpSocket.Close();
				}
			}
		}

		private void OnServerAnnounce(byte[] packet, ref IPEndPoint from)
		{

			BsonDocument doc = null!;

			if (packet.Length - ServerAnnounceHeader.Length > 0)
			{
				try
				{
					doc = BsonSerializer.Deserialize(packet, ServerAnnounceHeader.Length);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception deserializing game summary", e);
					return;
				}
			}

			ServerInfo info = new ServerInfo();
			info.Address = from;
			info.GameId = doc["gid"];
			info.PasswordProtected = doc["p"];

			info.GameStateFormatVersion = doc["fv"];
			info.GameStateFormatChecksum = doc["cs"];
			info.GameSummary = doc["s"].AsDocument;
			info.CurrentPlayers = doc["cc"].AsInt32;
			info.MaxPlayers = doc["mc"].AsInt32;

			if (ServersFound.TryAdd(from, info))
			{
				NotificationQueue.Enqueue(info);
			}


		}

		/// <summary>Re-sends the UDP search broadcast to discover any servers that may have come online since the initial search.</summary>
		public void Retry()
		{
			if (!Running)
			{
				return;
			}

			SendServerSearch();
		}

		private void SendServerSearch()
		{
			ImpunityLogger.LogDebug("Sending server search");

			IPEndPoint broadcastEp = new IPEndPoint(IPAddress.Broadcast, Options.ServerPort);
			FinderUdpSocket?.Send(SearchPacket, SearchPacket.Length, broadcastEp);
		}
	}
}

#else

using System;

namespace Impunity.Networking
{

	public class ImpunityTCPServerFinder : IDisposable
	{
		public ImpunityTCPServerFinder(ImpunityOptions options, Action<ServerInfo> onServerFound)
		{

		}

		/// <summary>Starts the UDP listener thread and sends the initial search broadcast.</summary>
		public void Start()
		{

		}

		public static void Cleanup()
		{

		}

		public void Dispose()
		{

		}

		/// <summary>Drains the notification queue and invokes the onServerFound callback for each newly discovered server. Must be called on the main thread (e.g., from Unity Update).</summary>
		public void Update()
		{

		}
		
		/// <summary>Re-sends the UDP search broadcast to discover any servers that may have come online since the initial search.</summary>
		public void Retry()
		{

		}
	}
}
#endif
