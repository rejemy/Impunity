// The replicated state of the swarm demo (TestGameScene): one channel, the arena, holding every client's movers.
//
// A mover follows a fixed closed curve. Its owner works out where it is from the shared server clock and sends that
// position many times a second, unguaranteed. The curve itself (MoverOrbit) is sent once, reliably, when the mover is
// created, so every other client can also work out where the mover really is right now and compare that with the
// last position it was sent: the gap between the two is the network's delay, made visible.
using System;
using System.IO;

using UnityEngine;

using UltraLiteDB;

using Impunity;
using Impunity.Connection;

namespace ImpunityDemo
{
	public static class DemoEntityTypes
	{
		// 0 is reserved
		public const int ARENA = 1;
		public const int MOVER = 2;
	}

	/// <summary>The curve a mover follows: an ellipse-like Lissajous figure around a centre, traced at a fixed rate
	/// against the server clock. Fixed for the mover's life.</summary>
	public struct MoverOrbit
	{
		public Vector2 Center;
		public Vector2 Radius;
		/// <summary>Angular speed on each axis, radians per second.</summary>
		public Vector2 Speed;
		public Vector2 Phase;
		/// <summary>Colour, as a hue from 0 to 1. Every mover of one client shares it.</summary>
		public float Hue;

		/// <summary>Where the mover is at a server time.</summary>
		public Vector2 PositionAt(long serverMs)
		{
			// Seconds within a long repeating window, so float precision holds however long the server has been up.
			double t = (serverMs % 3600000L) / 1000.0;
			return new Vector2(
				Center.x + Radius.x * (float)Math.Cos(Speed.x * t + Phase.x),
				Center.y + Radius.y * (float)Math.Sin(Speed.y * t + Phase.y));
		}

		/// <summary>How fast the mover is travelling at a server time, in world units per second.</summary>
		public float SpeedAt(long serverMs)
		{
			double t = (serverMs % 3600000L) / 1000.0;
			float vx = -Radius.x * Speed.x * (float)Math.Sin(Speed.x * t + Phase.x);
			float vy = Radius.y * Speed.y * (float)Math.Cos(Speed.y * t + Phase.y);
			return Mathf.Sqrt(vx * vx + vy * vy);
		}
	}

	/// <summary>One position report: where the mover was, and the server time at which its owner said so.</summary>
	public struct MoverSample
	{
		public Vector2 Position;
		/// <summary>Low 32 bits of the sender's server time in milliseconds. Enough to measure an age; wraps every 49 days.</summary>
		public uint SentMs;

		/// <summary>Milliseconds between this sample being sent and <paramref name="serverMs"/>.</summary>
		public int AgeAt(long serverMs)
		{
			return unchecked((int)((uint)serverMs - SentMs));
		}
	}

	/// <summary>Binary serializer for <see cref="MoverOrbit"/> (36 bytes: nine floats).</summary>
	public readonly struct MoverOrbitSerializer : IDistributableValueSerializer<MoverOrbit>
	{
		public void WriteTo(MoverOrbit value, BinaryWriter w)
		{
			w.Write(value.Center.x); w.Write(value.Center.y);
			w.Write(value.Radius.x); w.Write(value.Radius.y);
			w.Write(value.Speed.x); w.Write(value.Speed.y);
			w.Write(value.Phase.x); w.Write(value.Phase.y);
			w.Write(value.Hue);
		}

		public MoverOrbit ReadFrom(BinaryReader r, int byteCount)
		{
			MoverOrbit value = new MoverOrbit();
			value.Center = new Vector2(r.ReadSingle(), r.ReadSingle());
			value.Radius = new Vector2(r.ReadSingle(), r.ReadSingle());
			value.Speed = new Vector2(r.ReadSingle(), r.ReadSingle());
			value.Phase = new Vector2(r.ReadSingle(), r.ReadSingle());
			value.Hue = r.ReadSingle();
			return value;
		}

		public BsonValue ToBsonValue(MoverOrbit value)
		{
			return new BsonArray
			{
				value.Center.x, value.Center.y, value.Radius.x, value.Radius.y,
				value.Speed.x, value.Speed.y, value.Phase.x, value.Phase.y, value.Hue
			};
		}

		public MoverOrbit FromBsonValue(BsonValue value)
		{
			BsonArray a = value.AsArray!;
			MoverOrbit orbit = new MoverOrbit();
			orbit.Center = new Vector2(a[0].AsSingle, a[1].AsSingle);
			orbit.Radius = new Vector2(a[2].AsSingle, a[3].AsSingle);
			orbit.Speed = new Vector2(a[4].AsSingle, a[5].AsSingle);
			orbit.Phase = new Vector2(a[6].AsSingle, a[7].AsSingle);
			orbit.Hue = a[8].AsSingle;
			return orbit;
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }
		public DistributedValueSemantics ValueSemantics { get => DistributedValueSemantics.Immutable; }
	}

	/// <summary>Binary serializer for <see cref="MoverSample"/> (12 bytes: two floats and the send time).</summary>
	public readonly struct MoverSampleSerializer : IDistributableValueSerializer<MoverSample>
	{
		public void WriteTo(MoverSample value, BinaryWriter w)
		{
			w.Write(value.Position.x);
			w.Write(value.Position.y);
			w.Write(value.SentMs);
		}

		public MoverSample ReadFrom(BinaryReader r, int byteCount)
		{
			MoverSample value = new MoverSample();
			value.Position = new Vector2(r.ReadSingle(), r.ReadSingle());
			value.SentMs = r.ReadUInt32();
			return value;
		}

		public BsonValue ToBsonValue(MoverSample value)
		{
			return new BsonArray { value.Position.x, value.Position.y, (long)value.SentMs };
		}

		public MoverSample FromBsonValue(BsonValue value)
		{
			BsonArray a = value.AsArray!;
			MoverSample sample = new MoverSample();
			sample.Position = new Vector2(a[0].AsSingle, a[1].AsSingle);
			sample.SentMs = (uint)a[2].AsInt64;
			return sample;
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }
		public DistributedValueSemantics ValueSemantics { get => DistributedValueSemantics.Immutable; }
	}

	/// <summary>The one channel every demo client subscribes to. Its objects are the movers.</summary>
	[DistributedEntity(DemoEntityTypes.ARENA)]
	public partial class DemoArena : DistributedChannelBase
	{
		/// <summary>Free text any client may set; shown in the HUD. Sent reliably.</summary>
		public DistributedValue<string, StringSerializer> Banner;
	}

	/// <summary>One moving sprite. Owned by the client that created it (client-authoritative) and deleted when that
	/// client leaves.</summary>
	[DistributedEntity(DemoEntityTypes.MOVER)]
	public partial class DemoMover : DistributedObjectBase
	{
		/// <summary>The curve it follows. Set once, before the mover is created.</summary>
		public DistributedValue<MoverOrbit, MoverOrbitSerializer> Orbit;

		/// <summary>Its latest reported position. Written many times a second by its owner.</summary>
		public DistributedValue<MoverSample, MoverSampleSerializer> Motion;
	}
}
