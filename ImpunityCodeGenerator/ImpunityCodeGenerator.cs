using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SourceGenerator
{

	public class DistributedPropertyInfo
	{
		public string PropertyName { get; set; }

		public DistributedPropertyInfo(string name)
		{
			PropertyName = name;
		}

	}

	public class DistributedClassInfo
	{
		public string ClassName { get; set; }
		public string? Namespace { get; set; }
		public List<DistributedPropertyInfo> Properties { get; set; }

		public DistributedClassInfo(string name, string? nspace)
		{
			ClassName = name;
			Namespace = nspace;
			Properties = new List<DistributedPropertyInfo>();
		}
	}

	[Generator]
	public class DistributedEntityGenerator : ISourceGenerator
	{
		public static HashSet<string> IgnoreAssemblies = new HashSet<string>(new[]
			{ "UnityEngine.TestRunner", "UnityEditor.TestRunner", "Unity.VisualStudio.Editor", "Assembly-CSharp-Editor" });



		// IDE compilers (VS / VS Code's Roslyn language server) cache and reuse a single
		// generator instance across every project that references the analyzer, and may run
		// Execute concurrently — so all per-run state must be instance-level, reset at the
		// start of Execute, and guarded by ExecuteLock. (Batch builds hide bugs here: they
		// spin up a fresh instance per compilation.)
		private readonly object ExecuteLock = new object();

		public StringBuilder Output = new StringBuilder();
		public StringBuilder Info = new StringBuilder();

		public List<DistributedClassInfo> DistributedClasses = new List<DistributedClassInfo>();
		public string? SourceBasePath;

		public void Initialize(GeneratorInitializationContext context)
		{
			//WriteLine("Running Initialize");
		}

		private void LogNode(SyntaxNode node, string indent)
		{
			string summary = "";
			if (node is ClassDeclarationSyntax cds)
			{
				summary = cds.Identifier.Text;
			}
			else if (node is AttributeSyntax ats)
			{
				summary = ats.Name.ToString();
			}
			else if (node is AttributeArgumentListSyntax aals)
			{
				summary = aals.GetText().ToString();
			}
			else if (node is AttributeArgumentSyntax aas)
			{
				summary = aas.NameEquals + " " + aas.Expression;
			}
			else if (node is GenericNameSyntax gns)
			{
				summary = gns.Identifier.Text;
			}

			WriteInfo(indent + node.GetType().Name + " " + summary);
			foreach (var child in node.ChildNodes())
			{
				LogNode(child, indent + " ");
			}
		}

		public void WriteInfo(string line)
		{
			Info.AppendLine(line);
		}

		public void Execute(GeneratorExecutionContext context)
		{
			if (context.Compilation.AssemblyName == null)
			{
				throw new Exception("Must have an assembly name or this can't work");
			}

			if (IgnoreAssemblies.Contains(context.Compilation.AssemblyName))
			{
				return;
			}

			lock (ExecuteLock)
			{
				SourceBasePath = null;
				DistributedClasses.Clear();
				Output.Clear();
				Info.Clear();

				//WriteInfo("Running codegen 8 against " + context.Compilation.AssemblyName + " at " + DateTime.Now.ToString());

				foreach (var syntaxTree in context.Compilation.SyntaxTrees)
				{
					ExamineSyntaxTree(context, syntaxTree);
					//WriteInfo(syntaxTree.FilePath);
					//LogNode(syntaxTree.GetRoot(), "");
				}

				DistributedClasses.Sort((c1, c2) =>
				{
					int nameSpace = string.Compare(c1.Namespace, c2.Namespace);
					if (nameSpace != 0)
					{
						return nameSpace;
					}

					return string.Compare(c1.ClassName, c2.ClassName);
				});

				GenerateDistributedCode();

				//WriteInfo();

				if (DistributedClasses.Count > 0)
				{
					context.AddSource("ImpunityCode.generated.cs", Output.ToString());
				}
			}
		}



		private void WriteInfo()
		{
			string infoFilename = "/tmp/ImpunityGenInfo.txt";

			using (StreamWriter outputFile = new StreamWriter(infoFilename))
			{
				outputFile.WriteLine(Info.ToString());
				outputFile.WriteLine("Generated source:");
				outputFile.WriteLine(Output.ToString());
				outputFile.Flush();
			}
		}

		private void AddSourcePath(string path)
		{
			if (SourceBasePath == null)
			{
				SourceBasePath = path;
				return;
			}

			if (path.Length < SourceBasePath.Length)
			{
				SourceBasePath = path;
			}
		}

		private void GenerateDistributedCode()
		{
			Output.AppendLine("// Generated File - do not hand edit!\n");

			Output.AppendLine("using System.Collections.Generic;");
			Output.AppendLine("using System.IO;");
			Output.AppendLine("using UltraLiteDB;");

			string? currentNamespace = null;
			foreach (DistributedClassInfo classInfo in DistributedClasses)
			{
				if (classInfo.Namespace != currentNamespace)
				{
					if (currentNamespace != null)
					{
						Output.AppendLine("}\n");
					}

					Output.AppendLine("namespace " + classInfo.Namespace + "\n{\n");

					currentNamespace = classInfo.Namespace;
				}

				GenerateClassCode(classInfo);
			}

			if (currentNamespace != null)
			{
				Output.AppendLine("}\n");
			}
		}

		private void GenerateClassCode(DistributedClassInfo classInfo)
		{
			Output.AppendLine("\tpublic partial class " + classInfo.ClassName + "\n\t{");

			GenerateClassFieldInitializer(classInfo);

			foreach (DistributedPropertyInfo propInfo in classInfo.Properties)
			{
				GenerateDistributedFieldCode(propInfo);
			}

			Output.AppendLine("\t}\n");
		}

		private void GenerateClassFieldInitializer(DistributedClassInfo classInfo)
		{
			Output.AppendLine("\t\tpublic override void InitializeDistributedFields()\n\t\t{");
			Output.AppendLine("\t\tbase.InitializeDistributedFields();");

			if (classInfo.Properties.Count > 0)
			{
				// Wire ids are assigned per concrete runtime type over its whole inheritance chain, so resolve
				// through GetType() rather than this class: an inherited field's id depends on the full field set of
				// the object actually being constructed. ClientEntityManager.RegisterEntityType reads the same table.
				Output.AppendLine("\t\t\tvar _imp_fieldIds = Impunity.Connection.DistributedFieldIds.ForType(GetType());");

				foreach (DistributedPropertyInfo propInfo in classInfo.Properties)
				{
					Output.AppendLine($"\t\t\t{propInfo.PropertyName}._imp_Initialize(this, _imp_fieldIds[\"{propInfo.PropertyName}\"]);");
				}
			}

			Output.AppendLine("\t\t}\n");
		}

		private void GenerateDistributedFieldCode(DistributedPropertyInfo propInfo)
		{
			Output.AppendLine($@"		private void _imp_WriteChangesWrapper_{propInfo.PropertyName}(BinaryWriter w)
		{{
			{propInfo.PropertyName}.WriteChangesTo(w);
		}}");

			Output.AppendLine($@"		private void _imp_ReadInitialWrapper_{propInfo.PropertyName}(BinaryReader r)
		{{
			{propInfo.PropertyName}.ReadInitialFrom(r);
		}}");

			Output.AppendLine($@"		private void _imp_ReadChangeWrapper_{propInfo.PropertyName}(BinaryReader r)
		{{
			{propInfo.PropertyName}.ReadChangesFrom(r);
		}}");

			Output.AppendLine($@"		private void _imp_SkipWrapper_{propInfo.PropertyName}(BinaryReader r)
		{{
			{propInfo.PropertyName}.SkipFrom(r);
		}}");

			Output.AppendLine($@"		private BsonValue _imp_GetBsonValueWrapper_{propInfo.PropertyName}()
		{{
			return {propInfo.PropertyName}.GetAsBsonValue();
		}}");

			Output.AppendLine($@"		private void _imp_SetFromBsonValueWrapper_{propInfo.PropertyName}(BsonValue v)
		{{
			{propInfo.PropertyName}.SetFromBsonValue(v);
		}}");

		}


		private void ExamineSyntaxTree(GeneratorExecutionContext context, SyntaxTree fileTree)
		{
			bool generated = false;

			var classDeclarations = fileTree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>();
			foreach (var cd in classDeclarations)
			{
				if (ExamineClassDeclaration(context, cd))
				{
					generated = true;
				}
			}

			if (generated)
			{
				string sourcePath = Path.GetDirectoryName(fileTree.FilePath);
				AddSourcePath(sourcePath);
			}


		}

		private bool ExamineClassDeclaration(GeneratorExecutionContext context, ClassDeclarationSyntax cd)
		{
			bool generated = false;

			var attributeLists = cd.ChildNodes().OfType<AttributeListSyntax>();
			foreach (var attributeList in attributeLists)
			{
				foreach (AttributeSyntax attribute in attributeList.ChildNodes())
				{
					if (attribute.Name.ToString() == "DistributedEntity")
					{
						AnalyseDistributedClass(context, cd);
						generated = true;
					}
				}
			}

			if (!generated)
			{
				WarnAboutStrandedDistributedFields(context, cd);
			}

			return generated;
		}

		// A distributed field only replicates if it lives on a [DistributedEntity] type — nothing else generates the
		// serialization helpers or registers it. Since fields are now detected by their type rather than an attribute,
		// a field that has strayed onto an ordinary class would otherwise be silently inert, so say so.
		private void WarnAboutStrandedDistributedFields(GeneratorExecutionContext context, ClassDeclarationSyntax cd)
		{
			SemanticModel model = context.Compilation.GetSemanticModel(cd.SyntaxTree);

			// Check the merged type symbol, so another part of a partial class carrying the attribute counts.
			INamedTypeSymbol classSymbol = model.GetDeclaredSymbol(cd) as INamedTypeSymbol;
			if (classSymbol != null)
			{
				foreach (AttributeData attr in classSymbol.GetAttributes())
				{
					if (attr.AttributeClass != null && attr.AttributeClass.Name == "DistributedEntity")
					{
						return;
					}
				}
			}

			foreach (var fieldDecl in cd.ChildNodes().OfType<FieldDeclarationSyntax>())
			{
				if (!IsDistributedField(context, model, fieldDecl))
				{
					continue;
				}

				var msg = new DiagnosticDescriptor("IMP4", "Distributed field outside a distributed entity",
					"Field '" + fieldDecl.Declaration.Variables.First().Identifier.Text + "' is a distributed field, but "
					+ cd.Identifier.Text + " has no [DistributedEntity] attribute, so it will never be replicated",
					"Mismatch", DiagnosticSeverity.Warning, true);
				context.ReportDiagnostic(Diagnostic.Create(msg, fieldDecl.GetLocation()));
			}
		}

		// A field is distributed if — and only if — its type implements IDistributedField. Resolved through the
		// semantic model rather than by matching type names, so qualified names, aliases and any future field
		// container all work, and so the generator agrees exactly with the runtime's reflection check.
		private bool IsDistributedField(GeneratorExecutionContext context, SemanticModel model, FieldDeclarationSyntax fd)
		{
			foreach (SyntaxToken modifier in fd.Modifiers)
			{
				// Only instance fields replicate; the runtime enumerates with BindingFlags.Instance.
				if (modifier.IsKind(SyntaxKind.StaticKeyword) || modifier.IsKind(SyntaxKind.ConstKeyword))
				{
					return false;
				}
			}

			INamedTypeSymbol distributedFieldInterface =
				context.Compilation.GetTypeByMetadataName("Impunity.Connection.IDistributedField");
			if (distributedFieldInterface == null)
			{
				return false;
			}

			ITypeSymbol fieldType = model.GetTypeInfo(fd.Declaration.Type).Type;
			if (fieldType == null)
			{
				return false;
			}

			foreach (INamedTypeSymbol iface in fieldType.AllInterfaces)
			{
				if (SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, distributedFieldInterface))
				{
					return true;
				}
			}

			return false;
		}

		private void AnalyseDistributedClass(GeneratorExecutionContext context, ClassDeclarationSyntax cd)
		{
			string? classNamespace = GetNamespace(cd);

			DistributedClassInfo classInfo = new DistributedClassInfo(cd.Identifier.Text, classNamespace);

			WriteInfo("Found distributed class " + classInfo.Namespace + "." + classInfo.ClassName);

			SemanticModel model = context.Compilation.GetSemanticModel(cd.SyntaxTree);

			foreach (var fieldDecl in cd.ChildNodes().OfType<FieldDeclarationSyntax>())
			{
				bool isDistributed = IsDistributedField(context, model, fieldDecl);

				if (!isDistributed)
				{
					// [PersistAs] only means anything on a distributed field; anywhere else it is a mistake that
					// would otherwise store nothing.
					foreach (var attribute in fieldDecl.DescendantNodes().OfType<AttributeSyntax>())
					{
						string attrName = attribute.Name.ToString();
						if (attrName == "PersistAs" || attrName == "PersistAsAttribute")
						{
							var msg = new DiagnosticDescriptor("IMP5", "PersistAs on a non-distributed field",
								"[PersistAs] can only be applied to a distributed field (one whose type implements IDistributedField)",
								"Mismatch", DiagnosticSeverity.Error, true);
							context.ReportDiagnostic(Diagnostic.Create(msg, attribute.GetLocation()));
						}
					}
					continue;
				}

				AnalyseDistributedField(context, fieldDecl, classInfo);
			}

			DistributedClasses.Add(classInfo);
		}

		private void AnalyseDistributedField(GeneratorExecutionContext context, FieldDeclarationSyntax fd, DistributedClassInfo classInfo)
		{
			VariableDeclarationSyntax vd = fd.ChildNodes().OfType<VariableDeclarationSyntax>().First();
			if (vd.Variables.Count != 1)
			{
				var msg = new DiagnosticDescriptor("IMP2", "Multple variable declaration", "Declaring multiple variables per type not supported", "Mismatch", DiagnosticSeverity.Error, true);
				context.ReportDiagnostic(Diagnostic.Create(msg, vd.GetLocation()));

				return;
			}

			VariableDeclaratorSyntax varDef = vd.Variables.First();

			DistributedPropertyInfo propInfo = new DistributedPropertyInfo(varDef.Identifier.ToString());

			classInfo.Properties.Add(propInfo);

			WriteInfo("Found distributed field " + propInfo.PropertyName);
		}

		// determine the namespace the class/enum/struct is declared in, if any
		static string? GetNamespace(BaseTypeDeclarationSyntax syntax)
		{
			// If we don't have a namespace at all we'll return an empty string
			// This accounts for the "default namespace" case
			string? nameSpace = null;

			// Get the containing syntax node for the type declaration
			// (could be a nested type, for example)
			SyntaxNode? potentialNamespaceParent = syntax.Parent;

			// Keep moving "out" of nested classes etc until we get to a namespace
			// or until we run out of parents
			while (potentialNamespaceParent != null &&
					!(potentialNamespaceParent is NamespaceDeclarationSyntax))
			{
				potentialNamespaceParent = potentialNamespaceParent.Parent;
			}

			// Build up the final namespace by looping until we no longer have a namespace declaration
			if (potentialNamespaceParent is NamespaceDeclarationSyntax namespaceParent)
			{
				// We have a namespace. Use that as the type
				nameSpace = namespaceParent.Name.ToString();

				// Keep moving "out" of the namespace declarations until we 
				// run out of nested namespace declarations
				while (true)
				{
					if (!(namespaceParent.Parent is NamespaceDeclarationSyntax parent))
					{
						break;
					}

					// Add the outer namespace as a prefix to the final namespace
					nameSpace = $"{namespaceParent.Name}.{nameSpace}";
					namespaceParent = parent;
				}
			}

			// return the final namespace
			return nameSpace;
		}
	}
}
