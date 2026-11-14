using System;
using System.Collections.Generic;

using NUnit.Framework;

using UltraLiteDB;

using DevTools;

namespace DevTools.Tests
{
	// NetInspector's BSON → plain-object conversion: what `dev net_entity` hands the Pipeline's
	// JSON writer for a field dump.
	public class NetInspectorTests
	{
		[Test]
		public void ToPlain_ConvertsNestedBsonToDictionariesAndLists()
		{
			var doc = new BsonDocument
			{
				["hp"] = 12,
				["name"] = "oak",
				["fruit"] = new BsonArray { 1, 0, 2 },
				["inst"] = new BsonDocument { ["felled"] = true, ["at"] = 123456789L },
				["none"] = BsonValue.Null,
				["blob"] = new BsonValue(new byte[] { 1, 2, 3 }),
			};

			var plain = (Dictionary<string, object>)NetInspector.ToPlain(doc);
			Assert.AreEqual(12, plain["hp"]);
			Assert.AreEqual("oak", plain["name"]);
			CollectionAssert.AreEqual(new object[] { 1, 0, 2 }, (List<object>)plain["fruit"]);
			var inst = (Dictionary<string, object>)plain["inst"];
			Assert.AreEqual(true, inst["felled"]);
			Assert.AreEqual(123456789L, inst["at"]);
			Assert.IsNull(plain["none"]);
			Assert.AreEqual(Convert.ToBase64String(new byte[] { 1, 2, 3 }), plain["blob"]);
		}

		[Test]
		public void Json_CutsLongValuesOnlyWhenAsked()
		{
			var value = new BsonValue(new string('x', 50));
			Assert.AreEqual(52, NetInspector.Json(value, 0).Length);
			Assert.AreEqual(20, NetInspector.Json(value, 20).Length);
			StringAssert.EndsWith("...", NetInspector.Json(value, 20));
		}
	}
}
