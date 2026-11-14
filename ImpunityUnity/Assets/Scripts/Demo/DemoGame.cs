// The swarm demo (TestGameScene): each client spawns a handful of fast sprites and reports their positions to the
// server many times a second as unguaranteed updates; every other client draws them where they were last reported.
// Beside each remote sprite is a faint ring showing where it really is right now (worked out from the shared server
// clock), joined to it by a line. The length of that line is the network's delay, and the HUD puts numbers on it.
//
// What to watch: over TCP+UDP, or in a browser over WebSocket + a WebRTC data channel, the sprites sit close to
// their rings. Switch the mode to "guaranteed" (or use a WebSocket with no data channel) and add packet loss, and
// they fall behind in bursts: every lost segment holds up everything queued after it.
//
// Driven three ways, all through the same methods: the on-screen buttons, the dev console (`demo …`, which is what
// `tools/ucmd game demo …` reaches), and on WebGL the page (see DemoWebBridge).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using UnityEngine;
using UnityEngine.UI;

using DevTools;
using Dreamwing.Cons;

using Impunity;
using Impunity.Connection;
using Impunity.GameState;

namespace ImpunityDemo
{
	public class DemoGame : MonoBehaviour
	{
		public static DemoGame Instance { get; private set; }

		const string ArenaChannel = "arena";
		/// <summary>Must match the server's game_type_code (tools/demo-server): the UDP negotiation beside a TCP
		/// connection carries it, and a mismatch quietly leaves the connection on TCP alone.</summary>
		const string GameTypeCode = "ImpDemo";
		const int DefaultHttpPort = 29653;
		const int DefaultTcpPort = 29654;
		/// <summary>Half the height of the play field in world units (the camera's orthographic size).</summary>
		const float FieldHalfHeight = 5f;
		const float FieldHalfWidth = 8f;
		/// <summary>Seconds of samples the statistics cover.</summary>
		const float StatsWindow = 3f;

		// ----- Settings (buttons, console and page all change these)

		public string ServerUrl;
		public string World = "demo";
		public string Password = "demo";
		/// <summary>Position reports per second, per mover.</summary>
		public int SendRate = 30;
		/// <summary>True sends positions with SetUnguaranteed; false with Set, for comparison.</summary>
		public bool Unguaranteed = true;

		// ----- Connection state

		RemoteGameConnection Connection;
		DemoArena Arena;
		string TransportKind = "";
		/// <summary>idle, connecting, connected, or why it isn't.</summary>
		public string State { get; private set; } = "idle";
		int PendingSpawn;
		float Hue;
		float SendAccumulator;

		readonly List<DemoMover> Mine = new List<DemoMover>();
		readonly Dictionary<DemoMover, MoverView> Views = new Dictionary<DemoMover, MoverView>();

		// ----- Statistics over remote movers: one entry per position update received

		struct Sample { public float Time; public int AgeMs; public float ErrorUnits; }
		readonly Queue<Sample> Samples = new Queue<Sample>();
		readonly List<int> SortScratch = new List<int>();
		public DemoStats Stats { get; private set; } = new DemoStats();
		float NextStatsAt;

		// ----- Scene objects made in code

		Sprite DotSprite, RingSprite;
		Text HudText;
		InputField UrlField;
		Text ModeLabel, RateLabel;

		class MoverView
		{
			public DemoMover Mover;
			public bool IsMine;
			public Transform Dot;
			public Transform Ghost;
			public LineRenderer Lag;
		}

		void Awake()
		{
			Instance = this;
			Application.runInBackground = true;
			Application.targetFrameRate = 60;
			Hue = UnityEngine.Random.value;
			ServerUrl = DefaultServerUrl();

			SetUpCamera();
			DotSprite = MakeSprite(64, ring: false);
			RingSprite = MakeSprite(64, ring: true);
		}

		void Start()
		{
			// After DevToolsRoot.Awake (which brings up the console and the EventSystem).
			BuildUi();
			RegisterConsole();

			DevStatus.InGame = () => Connection != null && Connection.Connected && Arena != null;
			DevStatus.Details = () => StatusObject();
			NetInspector.ConnectionSource = () => Connection;
			NetInspector.CurrentChannel = () => Arena != null ? ArenaChannel : null;
			NetInspector.Me = () => Mine.Count > 0 ? Mine[0] : null;
			NetInspector.Labeler = e => e is DemoMover m ? (Mine.Contains(m) ? "my mover" : "remote mover") : null;

			DemoWebBridge.Start(this);
			ApplyStartupArguments();
		}

		void OnDestroy()
		{
			Disconnect();
			if (Instance == this)
			{
				Instance = null;
			}
		}

		// ═══════════════════════════════════════════════════════════
		// Connecting
		// ═══════════════════════════════════════════════════════════

		static string DefaultServerUrl()
		{
#if UNITY_WEBGL && !UNITY_EDITOR
			// The standalone server next to wherever this page came from.
			try
			{
				Uri page = new Uri(Application.absoluteURL);
				return (page.Scheme == "https" ? "wss://" : "ws://") + page.Host + ":" + DefaultHttpPort + "/ws";
			}
			catch (Exception)
			{
				return "ws://localhost:" + DefaultHttpPort + "/ws";
			}
#else
			return "tcp://127.0.0.1:" + DefaultTcpPort;
#endif
		}

		static GameStateFormat MakeFormat()
		{
			return new GameStateFormat(
				1,
				new GameStateCollection[]
				{
					new GameStateCollection { Index = 10, Name = "DemoNotes" }
				},
				new Type[] { typeof(DemoArena), typeof(DemoMover) });
		}

		/// <summary>Connects to <c>tcp://host:port</c>, <c>ws://host:port/ws</c> or <c>wss://…</c>, dropping any
		/// connection already open, and joins the arena.</summary>
		public void Connect(string url)
		{
			Disconnect();

			if (!string.IsNullOrEmpty(url))
			{
				ServerUrl = url.Trim();
			}
			if (UrlField != null)
			{
				UrlField.SetTextWithoutNotify(ServerUrl);
			}

			Uri uri;
			try
			{
				uri = new Uri(ServerUrl);
			}
			catch (Exception e)
			{
				State = "bad address: " + e.Message;
				return;
			}

			var options = new ImpunityOptions { GameTypeCode = GameTypeCode };
			try
			{
				if (uri.Scheme == "tcp")
				{
#if UNITY_WEBGL && !UNITY_EDITOR
					State = "a browser can't open TCP connections; use ws:// or wss://";
					return;
#else
					TransportKind = "tcp";
					Connection = RemoteGameConnection.MakeTCPRemoteConnection(uri.Host, uri.Port > 0 ? uri.Port : DefaultTcpPort, World, Password, MakeFormat(), options);
#endif
				}
				else if (uri.Scheme == "ws" || uri.Scheme == "wss")
				{
					TransportKind = "ws";
					Connection = RemoteGameConnection.MakeWebsocketRemoteConnection(ServerUrl, World, Password, MakeFormat(), options);
				}
				else
				{
					State = "unknown scheme '" + uri.Scheme + "' (tcp, ws or wss)";
					return;
				}
			}
			catch (Exception e)
			{
				State = "could not create the connection: " + e.Message;
				return;
			}

			RemoteGameConnection connection = Connection;
			State = "connecting";
			connection.OnNetworkError = (error) =>
			{
				if (Connection == connection)
				{
					DropConnection("connection lost: " + (error != null ? error.Message : "unknown"));
				}
			};

			connection.Connect((error) =>
			{
				if (Connection != connection)
				{
					return;
				}
				if (error != null)
				{
					DropConnection("connect failed: " + error.Message);
					return;
				}

				State = "joining";
				connection.EntityManager.SubscribeToChannel(ArenaChannel, new DemoArena(), (subscribeError, arena) =>
				{
					if (Connection != connection)
					{
						return;
					}
					if (subscribeError != null)
					{
						DropConnection("could not join the arena: " + subscribeError.Message);
						return;
					}

					Arena = arena;
					State = "connected";
					// A colour of this client's own, spread around the wheel by its connection id so two clients
					// rarely land close together.
					Hue = Mathf.Repeat(((uint)connection.ConnectionId.GetHashCode() % 1000) * 0.6180339887f, 1f);
					arena.OnObjectAddedEvent += (entity, newlyCreated) => AddView(entity as DemoMover);
					arena.OnObjectRemovedEvent += (entity) => RemoveView(entity as DemoMover);
					foreach (IDistributedObject existing in arena.DistributedObjects.Values)
					{
						AddView(existing as DemoMover);
					}

					int spawn = PendingSpawn;
					PendingSpawn = 0;
					if (spawn > 0)
					{
						Spawn(spawn);
					}
				});
			});
		}

		public void Disconnect()
		{
			if (Connection != null)
			{
				DropConnection("idle");
			}
		}

		void DropConnection(string newState)
		{
			RemoteGameConnection connection = Connection;
			Connection = null;
			Arena = null;
			State = newState;
			Mine.Clear();
			Samples.Clear();

			foreach (MoverView view in Views.Values)
			{
				Destroy(view.Dot.gameObject);
			}
			Views.Clear();

			try
			{
				connection?.Dispose();
			}
			catch (Exception e)
			{
				Debug.LogWarning("Error closing the connection: " + e.Message);
			}
		}

		/// <summary>Which wires this connection is using right now, in words.</summary>
		public string TransportLabel()
		{
			if (Connection == null || !Connection.Connected)
			{
				return "none";
			}

			bool datagrams = Connection.SupportsUnguaranteed;
			if (TransportKind == "tcp")
			{
				return datagrams ? "TCP + UDP" : "TCP only (no UDP yet)";
			}
#if UNITY_WEBGL && !UNITY_EDITOR
			return datagrams ? "WebSocket + WebRTC data channel" : "WebSocket only (no data channel)";
#else
			return "WebSocket only (the desktop client has no WebRTC)";
#endif
		}

		// ═══════════════════════════════════════════════════════════
		// Movers
		// ═══════════════════════════════════════════════════════════

		/// <summary>Adds <paramref name="count"/> movers of this client's own. Before the arena is joined, the request
		/// is remembered and carried out on joining.</summary>
		public void Spawn(int count)
		{
			if (Arena == null || Connection == null)
			{
				PendingSpawn += count;
				return;
			}

			long now = Connection.GetServerTime();
			for (int i = 0; i < count; i++)
			{
				MoverOrbit orbit = new MoverOrbit();
				orbit.Radius = new Vector2(UnityEngine.Random.Range(0.8f, 3f), UnityEngine.Random.Range(0.6f, 2.2f));
				orbit.Center = new Vector2(
					UnityEngine.Random.Range(-FieldHalfWidth + orbit.Radius.x + 0.3f, FieldHalfWidth - orbit.Radius.x - 0.3f),
					UnityEngine.Random.Range(-FieldHalfHeight + orbit.Radius.y + 0.3f, FieldHalfHeight - orbit.Radius.y - 1.2f));
				orbit.Speed = new Vector2(UnityEngine.Random.Range(1.5f, 4f), UnityEngine.Random.Range(1.5f, 4f));
				orbit.Phase = new Vector2(UnityEngine.Random.Range(0f, 6.28f), UnityEngine.Random.Range(0f, 6.28f));
				orbit.Hue = Hue;

				// Owned by this client: its writes apply here at once and are never echoed back, and the server
				// deletes the mover when this client goes away.
				DemoMover mover = new DemoMover { IsClientAuthoritative = true, DeleteOnDisconnect = true };
				mover.Orbit.Set(orbit);
				mover.Motion.Set(new MoverSample { Position = orbit.PositionAt(now), SentMs = (uint)now });

				RemoteGameConnection connection = Connection;
				connection.EntityManager.CreateObject(mover, Arena, false, (error, created) =>
				{
					if (Connection != connection || error != null)
					{
						if (error != null)
						{
							Debug.LogWarning("Could not create a mover: " + error.Message);
						}
						return;
					}
					Mine.Add(created);
					AddView(created);
				});
			}
		}

		/// <summary>Deletes this client's movers.</summary>
		public void Clear()
		{
			PendingSpawn = 0;
			foreach (DemoMover mover in Mine)
			{
				mover.Delete(null, (error, deleted) => { });
			}
			Mine.Clear();
		}

		void AddView(DemoMover mover)
		{
			if (mover == null || Views.ContainsKey(mover))
			{
				return;
			}

			// A mover this client is still creating can show up here before its creation is confirmed; Update notices
			// once it is in Mine.
			bool isMine = Mine.Contains(mover);
			MoverOrbit orbit = mover.Orbit.Get();
			Color color = Color.HSVToRGB(Mathf.Repeat(orbit.Hue, 1f), 0.75f, 1f);

			var view = new MoverView { Mover = mover };

			var dot = new GameObject("Mover " + mover.DistributedEntityId);
			dot.transform.SetParent(transform, false);
			dot.transform.localScale = Vector3.one * 0.28f;
			var dotRenderer = dot.AddComponent<SpriteRenderer>();
			dotRenderer.sprite = DotSprite;
			dotRenderer.color = color;
			dotRenderer.sortingOrder = 2;
			view.Dot = dot.transform;

			// Where the mover really is, and the gap to where we were last told it is. Only meaningful for other
			// clients' movers; ours are drawn where they are.
			var ghost = new GameObject("Truth");
			ghost.transform.SetParent(transform, false);
			ghost.transform.localScale = Vector3.one * 0.34f;
			var ghostRenderer = ghost.AddComponent<SpriteRenderer>();
			ghostRenderer.sprite = RingSprite;
			ghostRenderer.color = new Color(color.r, color.g, color.b, 0.45f);
			ghostRenderer.sortingOrder = 1;
			view.Ghost = ghost.transform;
			ghost.transform.SetParent(dot.transform, true);

			var lag = dot.AddComponent<LineRenderer>();
			lag.sharedMaterial = dotRenderer.sharedMaterial;
			lag.positionCount = 2;
			lag.useWorldSpace = true;
			lag.startWidth = lag.endWidth = 0.035f;
			lag.startColor = lag.endColor = new Color(color.r, color.g, color.b, 0.55f);
			lag.sortingOrder = 1;
			view.Lag = lag;

			Views[mover] = view;
			mover.Motion.OnChanged += (previous, current) => OnSample(view, current);
			SetOwnership(view, isMine);
			PlaceView(view);
		}

		void SetOwnership(MoverView view, bool isMine)
		{
			view.IsMine = isMine;
			view.Ghost.gameObject.SetActive(!isMine);
			view.Lag.enabled = !isMine;
		}

		void RemoveView(DemoMover mover)
		{
			if (mover != null && Views.TryGetValue(mover, out MoverView view))
			{
				Views.Remove(mover);
				Mine.Remove(mover);
				Destroy(view.Dot.gameObject);
			}
		}

		// A position update arrived for a mover (or, for our own, was just written).
		void OnSample(MoverView view, MoverSample sample)
		{
			if (view.IsMine || Connection == null)
			{
				return;
			}

			long now = Connection.GetServerTime();
			Vector2 truth = view.Mover.Orbit.Get().PositionAt(now);
			Samples.Enqueue(new Sample
			{
				Time = Time.unscaledTime,
				AgeMs = sample.AgeAt(now),
				ErrorUnits = Vector2.Distance(truth, sample.Position)
			});
		}

		void PlaceView(MoverView view)
		{
			Vector2 reported = view.Mover.Motion.Get().Position;
			view.Dot.position = new Vector3(reported.x, reported.y, 0f);

			if (!view.IsMine && Connection != null)
			{
				Vector2 truth = view.Mover.Orbit.Get().PositionAt(Connection.GetServerTime());
				view.Ghost.position = new Vector3(truth.x, truth.y, 0f);
				view.Lag.SetPosition(0, view.Dot.position);
				view.Lag.SetPosition(1, view.Ghost.position);
			}
		}

		// ═══════════════════════════════════════════════════════════
		// Per frame
		// ═══════════════════════════════════════════════════════════

		void Update()
		{
			// Write this frame's positions first: the connection's Update sends whatever is pending and then applies
			// what has arrived, so a position written after it would wait a whole frame before leaving.
			if (Connection != null && Connection.Connected && Arena != null)
			{
				SendPositions();
			}

			RemoteGameConnection connection = Connection;
			if (connection != null)
			{
				try
				{
					connection.Update();
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
			}

			if (Connection != null && Connection.Connected && Arena != null)
			{

				// Views made before their creation was confirmed don't know they are ours yet.
				foreach (MoverView view in Views.Values)
				{
					if (!view.IsMine && Mine.Contains(view.Mover))
					{
						SetOwnership(view, true);
					}
					PlaceView(view);
				}
			}

			if (Time.unscaledTime >= NextStatsAt)
			{
				NextStatsAt = Time.unscaledTime + 0.25f;
				RefreshStats();
				RefreshHud();
				DemoWebBridge.PublishStatus(StatusJson());
			}

			DemoWebBridge.Pump();
		}

		void SendPositions()
		{
			if (Mine.Count == 0 || SendRate <= 0)
			{
				return;
			}

			SendAccumulator += Time.unscaledDeltaTime * SendRate;
			if (SendAccumulator < 1f)
			{
				return;
			}
			// One report per mover per frame at most; a slow frame doesn't send a burst afterwards.
			SendAccumulator = Mathf.Min(SendAccumulator - 1f, 1f);

			long now = Connection.GetServerTime();
			foreach (DemoMover mover in Mine)
			{
				var sample = new MoverSample { Position = mover.Orbit.Get().PositionAt(now), SentMs = (uint)now };
				if (Unguaranteed)
				{
					mover.Motion.SetUnguaranteed(sample);
				}
				else
				{
					mover.Motion.Set(sample);
				}
			}
		}

		void RefreshStats()
		{
			float cutoff = Time.unscaledTime - StatsWindow;
			while (Samples.Count > 0 && Samples.Peek().Time < cutoff)
			{
				Samples.Dequeue();
			}

			var stats = new DemoStats();
			stats.MyMovers = Mine.Count;
			stats.RemoteMovers = Views.Count - Mine.Count;
			stats.Samples = Samples.Count;
			stats.UpdatesPerSecond = Samples.Count / StatsWindow;

			if (Samples.Count > 0)
			{
				SortScratch.Clear();
				double errorSum = 0;
				foreach (Sample sample in Samples)
				{
					SortScratch.Add(sample.AgeMs);
					errorSum += sample.ErrorUnits;
				}
				SortScratch.Sort();
				stats.AgeP50 = Percentile(SortScratch, 0.50f);
				stats.AgeP95 = Percentile(SortScratch, 0.95f);
				stats.AgeP99 = Percentile(SortScratch, 0.99f);
				stats.AgeMax = SortScratch[SortScratch.Count - 1];
				stats.MeanErrorUnits = (float)(errorSum / Samples.Count);
			}
			Stats = stats;
		}

		static int Percentile(List<int> sorted, float fraction)
		{
			return sorted[Mathf.Clamp(Mathf.CeilToInt(sorted.Count * fraction) - 1, 0, sorted.Count - 1)];
		}

		// ═══════════════════════════════════════════════════════════
		// Status, for the HUD, `tools/ucmd status` and the page
		// ═══════════════════════════════════════════════════════════

		object StatusObject()
		{
			DemoStats s = Stats;
			return new
			{
				state = State,
				server = ServerUrl,
				world = World,
				transport = TransportLabel(),
				datagrams = Connection != null && Connection.Connected && Connection.SupportsUnguaranteed,
				mode = Unguaranteed ? "unguaranteed" : "guaranteed",
				rate = SendRate,
				mine = s.MyMovers,
				remote = s.RemoteMovers,
				updatesPerSecond = Mathf.Round(s.UpdatesPerSecond),
				ageMs = new { p50 = s.AgeP50, p95 = s.AgeP95, p99 = s.AgeP99, max = s.AgeMax },
				meanErrorUnits = s.MeanErrorUnits,
				clockErrorMs = ClockErrorMs()
			};
		}

		string StatusJson()
		{
			DemoStats s = Stats;
			var json = new StringBuilder(256);
			json.Append("{\"state\":").Append(Quote(State));
			json.Append(",\"server\":").Append(Quote(ServerUrl));
			json.Append(",\"transport\":").Append(Quote(TransportLabel()));
			json.Append(",\"datagrams\":").Append(Connection != null && Connection.Connected && Connection.SupportsUnguaranteed ? "true" : "false");
			json.Append(",\"mode\":").Append(Quote(Unguaranteed ? "unguaranteed" : "guaranteed"));
			json.Append(",\"rate\":").Append(SendRate);
			json.Append(",\"mine\":").Append(s.MyMovers);
			json.Append(",\"remote\":").Append(s.RemoteMovers);
			json.Append(",\"updatesPerSecond\":").Append(Mathf.RoundToInt(s.UpdatesPerSecond));
			json.Append(",\"ageP50\":").Append(s.AgeP50);
			json.Append(",\"ageP95\":").Append(s.AgeP95);
			json.Append(",\"ageP99\":").Append(s.AgeP99);
			json.Append(",\"ageMax\":").Append(s.AgeMax);
			json.Append(",\"meanErrorUnits\":").Append(s.MeanErrorUnits.ToString("0.###", CultureInfo.InvariantCulture));
			json.Append(",\"clockErrorMs\":").Append(ClockErrorMs());
			json.Append(",\"fps\":").Append(Mathf.RoundToInt(1f / Mathf.Max(Time.smoothDeltaTime, 0.0001f)));
			json.Append('}');
			return json.ToString();
		}

		/// <summary>How far this client's idea of the server clock may be out, in milliseconds: half the round trip of
		/// the exchange it came from. The ages can be wrong by this much on each of the two clients involved.</summary>
		int ClockErrorMs()
		{
			if (Connection == null || Connection.ClockSyncRoundTripMillis < 0)
			{
				return -1;
			}
			return (int)((Connection.ClockSyncRoundTripMillis + 1) / 2);
		}

		static string Quote(string text)
		{
			return "\"" + (text ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ") + "\"";
		}

		void RefreshHud()
		{
			if (HudText == null)
			{
				return;
			}

			DemoStats s = Stats;
			var hud = new StringBuilder(400);
			hud.Append("<b>Impunity swarm demo</b>\n");
			hud.Append("server     ").Append(ServerUrl).Append("  (world ").Append(World).Append(")\n");
			hud.Append("state      ").Append(State);
			if (Connection != null && Connection.Connected)
			{
				hud.Append("  as ").Append(Connection.ConnectionId);
			}
			hud.Append('\n');
			hud.Append("transport  ").Append(TransportLabel()).Append('\n');
			hud.Append("sending    ").Append(s.MyMovers).Append(" movers at ").Append(SendRate).Append(" Hz, ")
				.Append(Unguaranteed ? "unguaranteed" : "guaranteed").Append('\n');
			hud.Append("receiving  ").Append(s.RemoteMovers).Append(" movers, ").Append(Mathf.RoundToInt(s.UpdatesPerSecond)).Append(" updates/s\n");
			if (s.Samples > 0)
			{
				hud.Append("age (ms)   p50 ").Append(s.AgeP50).Append("   p95 ").Append(s.AgeP95).Append("   p99 ").Append(s.AgeP99)
					.Append("   max ").Append(s.AgeMax).Append('\n');
				hud.Append("behind by  ").Append(s.MeanErrorUnits.ToString("0.00", CultureInfo.InvariantCulture)).Append(" units on average")
					.Append("   (clock ±").Append(ClockErrorMs()).Append(" ms)");
			}
			else
			{
				hud.Append("age (ms)   no remote updates yet");
			}
			HudText.text = hud.ToString();

			if (ModeLabel != null)
			{
				ModeLabel.text = Unguaranteed ? "Mode: unguaranteed" : "Mode: guaranteed";
			}
			if (RateLabel != null)
			{
				RateLabel.text = "Rate: " + SendRate + " Hz";
			}
		}

		// ═══════════════════════════════════════════════════════════
		// Console verbs
		// ═══════════════════════════════════════════════════════════

		void RegisterConsole()
		{
			Cons.AddCommandGroup("demo", "The swarm demo: connect to a server, spawn movers, change how they are sent");

			Cons.AddCommand<string, string, string>("demo connect", (url, world, password) =>
				{
					if (!string.IsNullOrEmpty(world)) World = world;
					if (!string.IsNullOrEmpty(password)) Password = password;
					Connect(url);
					Cons.Log("Connecting to " + ServerUrl + " (world " + World + "); state: " + State);
				},
				"Connects to a standalone server and joins the arena: tcp://host:29654 (TCP + UDP), ws://host:29653/ws or wss://… (WebSocket; in a browser, plus a WebRTC data channel). With no address, the last one",
				new CommandStringArgument("url", ""),
				new CommandStringArgument("world", ""),
				new CommandStringArgument("password", ""));

			Cons.AddCommand("demo disconnect", () => { Disconnect(); Cons.Log("Disconnected"); },
				"Closes the connection; the server deletes this client's movers");

			Cons.AddCommand<int>("demo spawn", (count) => { Spawn(count); Cons.Log("Spawning " + count + " movers"); },
				"Adds movers owned by this client (remembered until connected)",
				new CommandIntArgument("count", 16, 1, 2000));

			Cons.AddCommand("demo clear", () => { Clear(); Cons.Log("Deleted this client's movers"); },
				"Deletes this client's movers");

			Cons.AddCommand<int>("demo rate", (hz) => { SendRate = hz; Cons.Log("Sending positions at " + hz + " Hz"); },
				"Position reports per second for each of this client's movers (capped by the frame rate)",
				new CommandIntArgument("hz", null, 1, 240));

			Cons.AddCommand<string>("demo mode", (mode) =>
				{
					Unguaranteed = mode != "guaranteed";
					Cons.Log("Positions are now sent " + (Unguaranteed ? "unguaranteed" : "guaranteed"));
				},
				"How this client sends positions: unguaranteed (datagrams when the transport has them) or guaranteed (always the reliable connection)",
				new CommandStringArgument("mode") { PossibleValues = new[] { "unguaranteed", "guaranteed" } });

			Cons.AddCommand("demo status", () => Cons.Log(StatusJson()),
				"The demo's state and delay statistics as one line of JSON");
		}

		// Command line (desktop builds): -server <url> -world <w> -password <p> -spawn <n> -rate <hz> -mode <m>.
		// Page address (WebGL): ?server=…&world=…&password=…&spawn=…&rate=…&mode=…
		void ApplyStartupArguments()
		{
			Dictionary<string, string> arguments = DemoWebBridge.StartupArguments();
			if (arguments.TryGetValue("world", out string world)) World = world;
			if (arguments.TryGetValue("password", out string password)) Password = password;
			if (arguments.TryGetValue("rate", out string rate) && int.TryParse(rate, out int hz)) SendRate = Mathf.Clamp(hz, 1, 240);
			if (arguments.TryGetValue("mode", out string mode)) Unguaranteed = mode != "guaranteed";
			if (arguments.TryGetValue("spawn", out string spawn) && int.TryParse(spawn, out int count)) PendingSpawn = Mathf.Clamp(count, 0, 2000);
			if (arguments.TryGetValue("server", out string server))
			{
				Connect(server);
			}
			else if (UrlField != null)
			{
				UrlField.SetTextWithoutNotify(ServerUrl);
			}
		}

		// ═══════════════════════════════════════════════════════════
		// Scene and UI, built in code so the scene only needs this component
		// ═══════════════════════════════════════════════════════════

		void SetUpCamera()
		{
			Camera camera = Camera.main;
			if (camera == null)
			{
				camera = new GameObject("Main Camera", typeof(Camera)).GetComponent<Camera>();
				camera.tag = "MainCamera";
			}
			camera.orthographic = true;
			camera.orthographicSize = FieldHalfHeight;
			camera.transform.position = new Vector3(0f, 0f, -10f);
			camera.clearFlags = CameraClearFlags.SolidColor;
			camera.backgroundColor = new Color(0.05f, 0.06f, 0.09f);
		}

		static Sprite MakeSprite(int size, bool ring)
		{
			var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
			texture.filterMode = FilterMode.Bilinear;
			texture.wrapMode = TextureWrapMode.Clamp;
			float radius = size * 0.5f - 1f;
			var pixels = new Color32[size * size];
			for (int y = 0; y < size; y++)
			{
				for (int x = 0; x < size; x++)
				{
					float distance = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(size * 0.5f, size * 0.5f));
					float alpha = Mathf.Clamp01(radius - distance);
					if (ring)
					{
						alpha *= Mathf.Clamp01(distance - radius * 0.72f);
					}
					pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
				}
			}
			texture.SetPixels32(pixels);
			texture.Apply();
			return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
		}

		void BuildUi()
		{
			Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

			var canvasObject = new GameObject("DemoCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
			canvasObject.transform.SetParent(transform, false);
			var canvas = canvasObject.GetComponent<Canvas>();
			canvas.renderMode = RenderMode.ScreenSpaceOverlay;
			var scaler = canvasObject.GetComponent<CanvasScaler>();
			scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
			scaler.referenceResolution = new Vector2(1280, 720);
			scaler.matchWidthOrHeight = 0.5f;

			// HUD, top left.
			var hudPanel = NewRect("Hud", canvasObject.transform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), new Vector2(12, -12), new Vector2(560, 178));
			hudPanel.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);
			var hudTextRect = NewRect("Text", hudPanel, Vector2.zero, Vector2.one, new Vector2(0, 1), Vector2.zero, Vector2.zero);
			hudTextRect.offsetMin = new Vector2(10, 8);
			hudTextRect.offsetMax = new Vector2(-10, -8);
			HudText = hudTextRect.gameObject.AddComponent<Text>();
			HudText.font = font;
			HudText.fontSize = 17;
			HudText.color = new Color(0.9f, 0.93f, 1f);
			HudText.supportRichText = true;
			HudText.horizontalOverflow = HorizontalWrapMode.Overflow;
			HudText.verticalOverflow = VerticalWrapMode.Overflow;
			HudText.raycastTarget = false;

			// Controls, bottom left: the address, then a row of buttons.
			var urlRect = NewRect("ServerUrl", canvasObject.transform, Vector2.zero, Vector2.zero, Vector2.zero, new Vector2(12, 56), new Vector2(430, 34));
			urlRect.gameObject.AddComponent<Image>().color = new Color(0.12f, 0.14f, 0.2f, 0.95f);
			UrlField = urlRect.gameObject.AddComponent<InputField>();
			var urlTextRect = NewRect("Text", urlRect, Vector2.zero, Vector2.one, new Vector2(0, 0.5f), Vector2.zero, Vector2.zero);
			urlTextRect.offsetMin = new Vector2(8, 4);
			urlTextRect.offsetMax = new Vector2(-8, -4);
			var urlText = urlTextRect.gameObject.AddComponent<Text>();
			urlText.font = font;
			urlText.fontSize = 16;
			urlText.color = Color.white;
			urlText.alignment = TextAnchor.MiddleLeft;
			urlText.supportRichText = false;
			UrlField.textComponent = urlText;
			UrlField.lineType = InputField.LineType.SingleLine;
			UrlField.SetTextWithoutNotify(ServerUrl);
			UrlField.onEndEdit.AddListener((text) => ServerUrl = text.Trim());

			float x = 12;
			AddButton(canvasObject.transform, font, "Connect", ref x, 100, () => Connect(UrlField.text));
			AddButton(canvasObject.transform, font, "Disconnect", ref x, 110, Disconnect);
			AddButton(canvasObject.transform, font, "Spawn 16", ref x, 100, () => Spawn(16));
			AddButton(canvasObject.transform, font, "Clear", ref x, 70, Clear);
			ModeLabel = AddButton(canvasObject.transform, font, "Mode: unguaranteed", ref x, 190, () => Unguaranteed = !Unguaranteed);
			RateLabel = AddButton(canvasObject.transform, font, "Rate: 30 Hz", ref x, 120, () =>
			{
				SendRate = SendRate >= 60 ? 10 : SendRate >= 30 ? 60 : SendRate >= 20 ? 30 : 20;
			});
		}

		static RectTransform NewRect(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 position, Vector2 size)
		{
			var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
			rect.SetParent(parent, false);
			rect.anchorMin = anchorMin;
			rect.anchorMax = anchorMax;
			rect.pivot = pivot;
			rect.anchoredPosition = position;
			rect.sizeDelta = size;
			return rect;
		}

		static Text AddButton(Transform parent, Font font, string caption, ref float x, float width, Action onClick)
		{
			var rect = NewRect(caption.Split(':')[0].Replace(" ", "") + "Button", parent, Vector2.zero, Vector2.zero, Vector2.zero, new Vector2(x, 12), new Vector2(width, 36));
			x += width + 8;
			var image = rect.gameObject.AddComponent<Image>();
			image.color = new Color(0.2f, 0.32f, 0.55f, 0.95f);
			var button = rect.gameObject.AddComponent<Button>();
			button.targetGraphic = image;
			button.onClick.AddListener(() => onClick());

			var labelRect = NewRect("Text", rect, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
			var label = labelRect.gameObject.AddComponent<Text>();
			label.font = font;
			label.fontSize = 16;
			label.color = Color.white;
			label.alignment = TextAnchor.MiddleCenter;
			label.text = caption;
			label.raycastTarget = false;
			return label;
		}
	}

	/// <summary>What the demo measured over the last few seconds, for other clients' movers.</summary>
	public class DemoStats
	{
		public int MyMovers;
		public int RemoteMovers;
		/// <summary>Position updates in the window.</summary>
		public int Samples;
		public float UpdatesPerSecond;
		/// <summary>How old a position was when it was applied here, in milliseconds.</summary>
		public int AgeP50, AgeP95, AgeP99, AgeMax;
		/// <summary>Average distance between a mover's reported and true position when an update arrived, in world units.</summary>
		public float MeanErrorUnits;
	}
}
