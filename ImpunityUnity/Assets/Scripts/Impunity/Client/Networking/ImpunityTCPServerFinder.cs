using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;


using UltraLiteDB;

namespace Impunity.Networking
{


	public class ImpunityTCPServerFinder : IDisposable
	{
		ImpunityOptions Options;
		Thread UDPListenerThread;
		UdpClient FinderUdpSocket;
		bool Running;

		byte[] SearchPacket;

		Action<ServerInfo> OnServerFoundCallback;

		ConcurrentDictionary<IPEndPoint, ServerInfo> ServersFound;
		ConcurrentQueue<ServerInfo> NotificationQueue;

		byte[] ServerAnnounceHeader;

		public ImpunityTCPServerFinder(ImpunityOptions options, Action<ServerInfo> onServerFound)
		{
			Options = options;
			OnServerFoundCallback = onServerFound;

			ServerAnnounceHeader = Encoding.UTF8.GetBytes(ImpunityConstants.ServerAnnouncePacketHeader + Options.GameTypeCode + ":");

			ServersFound = new ConcurrentDictionary<IPEndPoint, ServerInfo>();
			NotificationQueue = new ConcurrentQueue<ServerInfo>();

			SearchPacket = Encoding.UTF8.GetBytes(ImpunityConstants.ServerSearchPacketHeader + Options.GameTypeCode + ":");
		}

		public void Start()
		{
			Running = true;
			UDPListenerThread = new Thread(new ThreadStart(UDPListener));
			UDPListenerThread.IsBackground = true;
			UDPListenerThread.Name = "Server Finder UDP";
			UDPListenerThread.Start();
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
				IPEndPoint groupEP = new IPEndPoint(IPAddress.Any, Options.ClientPort);

				SendServerSearch();

				while (Running)
				{
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

			BsonDocument doc = null;

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

			IPEndPoint broadcastEp = new IPEndPoint(IPAddress.Any, Options.ServerPort);
			FinderUdpSocket.SendAsync(SearchPacket, SearchPacket.Length, broadcastEp);
		}
	}

}