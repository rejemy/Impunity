using System;
using System.IO;
using NUnit.Framework;

using Impunity;
using Impunity.Connection;
using Impunity.GameState;

using UltraLiteDB;

public class ImpunityUnitTests
{
	// ───────── Helpers ─────────

	static (MemoryStream ms, BinaryWriter w, BinaryReader r) MakeStream()
	{
		var ms = new MemoryStream();
		return (ms, new BinaryWriter(ms), new BinaryReader(ms));
	}

	static void ResetStream(MemoryStream ms) => ms.Position = 0;

	/// <summary>Round-trip helper for client-side serializers.</summary>
	static T RoundTrip<T, S>(S ser, T value) where S : IDistributableValueSerializer<T>
	{
		var (ms, w, r) = MakeStream();
		ser.WriteTo(value, w);
		ResetStream(ms);
		return ser.ReadFrom(r);
	}

	/// <summary>Round-trip helper for server-side value types.</summary>
	static T RoundTripServer<T>(T val) where T : struct, IDistributableValueType
	{
		var (ms, w, r) = MakeStream();
		val.WriteTo(w);
		ResetStream(ms);
		var result = default(T);
		result.ReadFrom(r);
		return result;
	}

	// ───────── 1. Client-Side Serializer Round-Trips ─────────

	[Test, Category("Serializers")]
	public void BoolSerializer_RoundTrip()
	{
		Assert.AreEqual(true, RoundTrip(new BoolSerializer(), true));
		Assert.AreEqual(false, RoundTrip(new BoolSerializer(), false));
	}

	[Test, Category("Serializers")]
	public void Int8Serializer_RoundTrip()
	{
		Assert.AreEqual((sbyte)-42, RoundTrip(new Int8Serializer(), (sbyte)-42));
		Assert.AreEqual(sbyte.MinValue, RoundTrip(new Int8Serializer(), sbyte.MinValue));
		Assert.AreEqual(sbyte.MaxValue, RoundTrip(new Int8Serializer(), sbyte.MaxValue));
	}

	[Test, Category("Serializers")]
	public void UInt8Serializer_RoundTrip()
	{
		Assert.AreEqual((byte)200, RoundTrip(new UInt8Serializer(), (byte)200));
		Assert.AreEqual(byte.MaxValue, RoundTrip(new UInt8Serializer(), byte.MaxValue));
	}

	[Test, Category("Serializers")]
	public void Int16Serializer_RoundTrip()
	{
		Assert.AreEqual((short)-12345, RoundTrip(new Int16Serializer(), (short)-12345));
	}

	[Test, Category("Serializers")]
	public void UInt16Serializer_RoundTrip()
	{
		Assert.AreEqual((ushort)65535, RoundTrip(new UInt16Serializer(), (ushort)65535));
	}

	[Test, Category("Serializers")]
	public void Int32Serializer_RoundTrip()
	{
		Assert.AreEqual(int.MinValue, RoundTrip(new Int32Serializer(), int.MinValue));
		Assert.AreEqual(42, RoundTrip(new Int32Serializer(), 42));
	}

	[Test, Category("Serializers")]
	public void UInt32Serializer_RoundTrip()
	{
		Assert.AreEqual(uint.MaxValue, RoundTrip(new UInt32Serializer(), uint.MaxValue));
	}

	[Test, Category("Serializers")]
	public void Int64Serializer_RoundTrip()
	{
		Assert.AreEqual(long.MinValue, RoundTrip(new Int64Serializer(), long.MinValue));
	}

	[Test, Category("Serializers")]
	public void UInt64Serializer_RoundTrip()
	{
		Assert.AreEqual(ulong.MaxValue, RoundTrip(new UInt64Serializer(), ulong.MaxValue));
	}

	[Test, Category("Serializers")]
	public void FloatSerializer_RoundTrip()
	{
		Assert.AreEqual(3.14f, RoundTrip(new FloatSerializer(), 3.14f));
		Assert.AreEqual(float.NegativeInfinity, RoundTrip(new FloatSerializer(), float.NegativeInfinity));
	}

	[Test, Category("Serializers")]
	public void DoubleSerializer_RoundTrip()
	{
		Assert.AreEqual(2.718281828d, RoundTrip(new DoubleSerializer(), 2.718281828d));
	}

	[Test, Category("Serializers")]
	public void DecimalSerializer_RoundTrip()
	{
		Assert.AreEqual(99999.99999m, RoundTrip(new DecimalSerializer(), 99999.99999m));
	}

	[Test, Category("Serializers")]
	public void CharSerializer_RoundTrip()
	{
		Assert.AreEqual('Z', RoundTrip(new CharSerializer(), 'Z'));
		Assert.AreEqual('\u00E9', RoundTrip(new CharSerializer(), '\u00E9'));
	}

	[Test, Category("Serializers")]
	public void StringSerializer_RoundTrip()
	{
		Assert.AreEqual("hello world", RoundTrip(new StringSerializer(), "hello world"));
	}

	[Test, Category("Serializers")]
	public void StringSerializer_Empty()
	{
		Assert.AreEqual("", RoundTrip(new StringSerializer(), ""));
	}

	[Test, Category("Serializers")]
	public void StringSerializer_Null()
	{
		Assert.IsNull(RoundTrip(new StringSerializer(), (string)null));
	}

	[Test, Category("Serializers")]
	public void BlobSerializer_RoundTrip()
	{
		var data = new ArraySegment<byte>(new byte[] { 1, 2, 3, 4, 5 });
		var result = RoundTrip(new BlobSerializer(), data);
		Assert.AreEqual(data.Count, result.Count);
		for (int i = 0; i < data.Count; i++)
			Assert.AreEqual(data.Array[i], result.Array[i]);
	}

	[Test, Category("Serializers")]
	public void BlobSerializer_Null()
	{
		var result = RoundTrip(new BlobSerializer(), default(ArraySegment<byte>));
		Assert.IsNull(result.Array);
	}

	[Test, Category("Serializers")]
	public void DateTimeSerializer_RoundTrip()
	{
		var dt = new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc);
		Assert.AreEqual(dt, RoundTrip(new DateTimeSerializer(), dt));
	}

	[Test, Category("Serializers")]
	public void DateTimeOffsetSerializer_RoundTrip()
	{
		var dto = new DateTimeOffset(2025, 6, 15, 10, 30, 0, TimeSpan.FromMinutes(30));
		var result = RoundTrip(new DateTimeOffsetSerializer(), dto);
		Assert.AreEqual(dto.Ticks, result.Ticks);
		Assert.AreEqual(dto.Offset.TotalMinutes, result.Offset.TotalMinutes);
	}

	[Test, Category("Serializers")]
	public void TimeSpanSerializer_RoundTrip()
	{
		var ts = TimeSpan.FromHours(3.5);
		Assert.AreEqual(ts, RoundTrip(new TimeSpanSerializer(), ts));
	}

	[Test, Category("Serializers")]
	public void GuidSerializer_RoundTrip()
	{
		var g = Guid.NewGuid();
		Assert.AreEqual(g, RoundTrip(new GuidSerializer(), g));
	}

	// ───────── BSON Custom Serializers ─────────

	class TestPoco
	{
		public int X { get; set; }
		public string Y { get; set; }
	}

	[Test, Category("Serializers")]
	public void BsonSmallSerializer_RoundTrip()
	{
		var ser = new BsonSmallSerializer<TestPoco>();
		var obj = new TestPoco { X = 42, Y = "test" };
		var result = RoundTrip(ser, obj);
		Assert.AreEqual(42, result.X);
		Assert.AreEqual("test", result.Y);
	}

	[Test, Category("Serializers")]
	public void BsonSmallSerializer_Null()
	{
		var ser = new BsonSmallSerializer<TestPoco>();
		Assert.IsNull(RoundTrip(ser, (TestPoco)null));
	}

	[Test, Category("Serializers")]
	public void BsonSerializer_RoundTrip()
	{
		var ser = new BsonSerializer<TestPoco>();
		var obj = new TestPoco { X = 99, Y = "large" };
		var result = RoundTrip(ser, obj);
		Assert.AreEqual(99, result.X);
		Assert.AreEqual("large", result.Y);
	}

	[Test, Category("Serializers")]
	public void BsonSerializer_Null()
	{
		var ser = new BsonSerializer<TestPoco>();
		Assert.IsNull(RoundTrip(ser, (TestPoco)null));
	}

	// ───────── 2. Server-Side Value Type Round-Trips ─────────

	[Test, Category("ServerValueTypes")]
	public void DBool_RoundTrip()
	{
		DBool val = true;
		Assert.AreEqual(true, (bool)RoundTripServer(val));
	}

	[Test, Category("ServerValueTypes")]
	public void DInt8_RoundTrip()
	{
		DInt8 val = (sbyte)-42;
		Assert.AreEqual((sbyte)-42, (sbyte)RoundTripServer(val));
	}

	[Test, Category("ServerValueTypes")]
	public void DUInt8_RoundTrip()
	{
		DUInt8 val = (byte)200;
		Assert.AreEqual((byte)200, (byte)RoundTripServer(val));
	}

	[Test, Category("ServerValueTypes")]
	public void DInt16_RoundTrip()
	{
		DInt16 val = (short)-12345;
		Assert.AreEqual((short)-12345, (short)RoundTripServer(val));
	}

	[Test, Category("ServerValueTypes")]
	public void DUInt16_RoundTrip()
	{
		DUInt16 val = (ushort)65535;
		Assert.AreEqual((ushort)65535, (ushort)RoundTripServer(val));
	}

	[Test, Category("ServerValueTypes")]
	public void DInt32_RoundTrip()
	{
		DInt32 val = int.MinValue;
		Assert.AreEqual(int.MinValue, (int)RoundTripServer(val));
	}

	[Test, Category("ServerValueTypes")]
	public void DUInt32_RoundTrip()
	{
		DUInt32 val = uint.MaxValue;
		Assert.AreEqual(uint.MaxValue, (uint)RoundTripServer(val));
	}

	[Test, Category("ServerValueTypes")]
	public void DInt64_RoundTrip()
	{
		DInt64 val = long.MinValue;
		Assert.AreEqual(long.MinValue, (long)RoundTripServer(val));
	}

	[Test, Category("ServerValueTypes")]
	public void DUInt64_RoundTrip()
	{
		DUInt64 val = ulong.MaxValue;
		Assert.AreEqual(ulong.MaxValue, (ulong)RoundTripServer(val));
	}

	[Test, Category("ServerValueTypes")]
	public void DFloat_RoundTrip()
	{
		DFloat val = 3.14f;
		Assert.AreEqual(3.14f, (float)RoundTripServer(val));
	}

	[Test, Category("ServerValueTypes")]
	public void DDouble_RoundTrip()
	{
		DDouble val = 2.718d;
		Assert.AreEqual(2.718d, (double)RoundTripServer(val));
	}

	[Test, Category("ServerValueTypes")]
	public void DDecimal_RoundTrip()
	{
		DDecimal val = 99999.99999m;
		Assert.AreEqual(99999.99999m, (decimal)RoundTripServer(val));
	}

	[Test, Category("ServerValueTypes")]
	public void DChar_RoundTrip()
	{
		DChar val = 'Z';
		Assert.AreEqual('Z', (char)RoundTripServer(val));
	}

	[Test, Category("ServerValueTypes")]
	public void DString_RoundTrip()
	{
		DString val = "hello";
		Assert.AreEqual("hello", (string)RoundTripServer(val));
	}

	[Test, Category("ServerValueTypes")]
	public void DDateTime_RoundTrip()
	{
		var dt = new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc);
		DDateTime val = dt;
		Assert.AreEqual(dt, (DateTime)RoundTripServer(val));
	}

	[Test, Category("ServerValueTypes")]
	public void DTimeSpan_RoundTrip()
	{
		var ts = TimeSpan.FromHours(3.5);
		DTimeSpan val = ts;
		Assert.AreEqual(ts, (TimeSpan)RoundTripServer(val));
	}

	[Test, Category("ServerValueTypes")]
	public void DGuid_RoundTrip()
	{
		var g = Guid.NewGuid();
		DGuid val = g;
		Assert.AreEqual(g, (Guid)RoundTripServer(val));
	}

	// ───────── 3. Utility Functions ─────────

	[Test, Category("Utilities")]
	public void CompareArraySegments_Equal()
	{
		var a = new ArraySegment<byte>(new byte[] { 1, 2, 3 });
		var b = new ArraySegment<byte>(new byte[] { 1, 2, 3 });
		Assert.AreEqual(0, ImpunityUtil.CompareArraySegments(a, b));
	}

	[Test, Category("Utilities")]
	public void CompareArraySegments_LessThan()
	{
		var a = new ArraySegment<byte>(new byte[] { 1, 2, 3 });
		var b = new ArraySegment<byte>(new byte[] { 1, 2, 4 });
		Assert.Less(ImpunityUtil.CompareArraySegments(a, b), 0);
	}

	[Test, Category("Utilities")]
	public void CompareArraySegments_DifferentLengths()
	{
		var a = new ArraySegment<byte>(new byte[] { 1, 2 });
		var b = new ArraySegment<byte>(new byte[] { 1, 2, 3 });
		Assert.Less(ImpunityUtil.CompareArraySegments(a, b), 0);
		Assert.Greater(ImpunityUtil.CompareArraySegments(b, a), 0);
	}

	[Test, Category("Utilities")]
	public void CompareArraySegments_NullHandling()
	{
		var a = new ArraySegment<byte>(new byte[] { 1 });
		var nil = default(ArraySegment<byte>);
		Assert.AreEqual(0, ImpunityUtil.CompareArraySegments(nil, nil));
		Assert.Less(ImpunityUtil.CompareArraySegments(nil, a), 0);
		Assert.Greater(ImpunityUtil.CompareArraySegments(a, nil), 0);
	}

	[Test, Category("Utilities")]
	public void KeyStringToInt_And_KeyIntToString_RoundTrip()
	{
		Assert.AreEqual(255, ImpunityUtil.KeyStringToInt("FF"));
		Assert.AreEqual("FF", ImpunityUtil.KeyIntToString(255));
		Assert.AreEqual(0, ImpunityUtil.KeyStringToInt(ImpunityUtil.KeyIntToString(0)));
		Assert.AreEqual(4096, ImpunityUtil.KeyStringToInt(ImpunityUtil.KeyIntToString(4096)));
	}

	[Test, Category("Utilities")]
	public void StartsWith_Match()
	{
		Assert.IsTrue(ImpunityUtil.StartsWith(new byte[] { 1, 2, 3, 4 }, new byte[] { 1, 2, 3 }));
	}

	[Test, Category("Utilities")]
	public void StartsWith_Mismatch()
	{
		Assert.IsFalse(ImpunityUtil.StartsWith(new byte[] { 1, 2, 3 }, new byte[] { 1, 9, 3 }));
	}

	[Test, Category("Utilities")]
	public void StartsWith_PacketShorterThanHeader()
	{
		Assert.IsFalse(ImpunityUtil.StartsWith(new byte[] { 1 }, new byte[] { 1, 2, 3 }));
	}

	[Test, Category("Utilities")]
	public void HashPassword_NullReturnsNull()
	{
		Assert.IsNull(ImpunityUtil.HashPassword(null));
	}

	[Test, Category("Utilities")]
	public void HashPassword_Consistent()
	{
		string h1 = ImpunityUtil.HashPassword("test123");
		string h2 = ImpunityUtil.HashPassword("test123");
		Assert.AreEqual(h1, h2);
		Assert.IsNotEmpty(h1);
	}

	[Test, Category("Utilities")]
	public void HashPassword_DifferentInputsDifferentOutputs()
	{
		Assert.AreNotEqual(ImpunityUtil.HashPassword("a"), ImpunityUtil.HashPassword("b"));
	}

	// ───────── 4. Sequence Number Wraparound ─────────

	[Test, Category("SequenceNumbers")]
	public void SeqCompare_NormalOrder()
	{
		ushort newer = 5, older = 3;
		Assert.IsTrue((short)(newer - older) > 0);
	}

	[Test, Category("SequenceNumbers")]
	public void SeqCompare_OutOfOrder()
	{
		ushort newer = 5, older = 3;
		Assert.IsFalse((short)(older - newer) > 0);
	}

	[Test, Category("SequenceNumbers")]
	public void SeqCompare_Equal()
	{
		ushort a = 100;
		Assert.IsFalse((short)(a - a) > 0);
	}

	[Test, Category("SequenceNumbers")]
	public void SeqCompare_Wraparound()
	{
		ushort newer = 0;
		ushort older = ushort.MaxValue;
		// 0 is "newer" than 65535 after wraparound
		Assert.IsTrue((short)(newer - older) > 0);
	}

	[Test, Category("SequenceNumbers")]
	public void SeqCompare_LargeGap()
	{
		ushort a = 40000, b = 10000;
		Assert.IsTrue((short)(a - b) > 0);
		Assert.IsFalse((short)(b - a) > 0);
	}

	// ───────── 5. Buffer Pool ─────────

	[Test, Category("BufferPool")]
	public void SmallBuffer_IsUsable()
	{
		var buf = ImpunityUtil.GetSmallBuffer();
		Assert.IsNotNull(buf);
		Assert.IsTrue(buf.IsSmall);
		Assert.IsNotNull(buf.Bytes);
		Assert.IsNotNull(buf.Writer);
		Assert.IsNotNull(buf.Reader);
		buf.Dispose();
	}

	[Test, Category("BufferPool")]
	public void SmallBuffer_ReturnAndReuse()
	{
		var buf1 = ImpunityUtil.GetSmallBuffer();
		buf1.Dispose();
		var buf2 = ImpunityUtil.GetSmallBuffer();
		Assert.AreSame(buf1, buf2);
		buf2.Dispose();
	}

	[Test, Category("BufferPool")]
	public void LargeBuffer_IsUsable()
	{
		var buf = ImpunityUtil.GetLargeBuffer();
		Assert.IsNotNull(buf);
		Assert.IsFalse(buf.IsSmall);
		buf.Dispose();
	}

	[Test, Category("BufferPool")]
	public void LargeBuffer_ReturnAndReuse()
	{
		var buf1 = ImpunityUtil.GetLargeBuffer();
		buf1.Dispose();
		var buf2 = ImpunityUtil.GetLargeBuffer();
		Assert.AreSame(buf1, buf2);
		buf2.Dispose();
	}
}
