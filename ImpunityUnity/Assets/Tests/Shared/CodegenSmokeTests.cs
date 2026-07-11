using System;
using NUnit.Framework;

using Impunity.Connection;

namespace Impunity.Tests
{
	// If the ImpunityCodeGenerator analyzer is not running for this compilation, this type compiles
	// but its distributed fields stay null (the field-initializing constructor is generated).
	[DistributedEntity(999)]
	public partial class CodegenSmokeEntity : DistributedObjectBase
	{
		[Distributed(1)]
		public DistributedValue<int, Int32Serializer> Value;
	}

	/// <summary>Verifies the Impunity source generator ran over this test assembly — the load-bearing
	/// assumption for every distributed-entity test in the suite.</summary>
	public class CodegenSmokeTests
	{
		[Test]
		public void GeneratorInitializedDistributedFields()
		{
			var entity = new CodegenSmokeEntity();
			Assert.IsNotNull(entity.Value,
				"Distributed field was null — the ImpunityCodeGenerator analyzer did not run for this compilation");
		}

		[Test]
		public void GeneratedEntityTypeRegisters()
		{
			var manager = new ClientEntityManager();
			var defs = manager.RegisterEntityTypes(new Type[] { typeof(CodegenSmokeEntity) });
			Assert.IsNotNull(defs);
			Assert.AreEqual(1, defs!.Length);
		}
	}
}
