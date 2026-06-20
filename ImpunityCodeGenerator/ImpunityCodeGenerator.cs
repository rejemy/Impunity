using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SourceGenerator
{

	public class DistributedPropertyInfo
	{
		public string PropertyName { get; set; }
		public string PropertyFieldType { get; set; }
		public string PropertyDType { get; set; }
		public string PropertyId { get; set; }


		public DistributedPropertyInfo(string name, string fieldType, string dtype, string propId)
		{
			PropertyName = name;
			PropertyFieldType = fieldType;
			PropertyDType = dtype;
			PropertyId = propId;
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
		public static HashSet<string> IgnoreAssemblies = new HashSet<string>(new []
			{ "UnityEngine.TestRunner", "UnityEditor.TestRunner", "Unity.VisualStudio.Editor", "Assembly-CSharp-Editor" });

		public static HashSet<string> ValidDistributedFieldTypes = new HashSet<string>(new[]
			{ "DistributedValue", "DistributedArray", "DistributedQueue", "DistributedIntDictionary", "DistributedStringDictionary",
			  "DistributedTemporalValue", "DistributedTemporalArray", "DistributedTemporalQueue", "DistributedTemporalIntDictionary", "DistributedTemporalStringDictionary" });


		public static StringBuilder Output = new StringBuilder();
		public static StringBuilder Info = new StringBuilder();
		
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
			foreach(var child in node.ChildNodes())
			{
				LogNode(child, indent + " ");
			}
		}

		public static void WriteInfo(string line)
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

			SourceBasePath = null;
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

			if(path.Length < SourceBasePath.Length)
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
			foreach(DistributedClassInfo classInfo in DistributedClasses)
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

			foreach(DistributedPropertyInfo propInfo in classInfo.Properties)
			{
				GenerateDistributedFieldCode(propInfo);
			}

			Output.AppendLine("\t}\n");
		}

		private void GenerateClassFieldInitializer(DistributedClassInfo classInfo)
		{
			Output.AppendLine("\t\tpublic override void InitializeDistributedFields()\n\t\t{");
			Output.AppendLine("\t\tbase.InitializeDistributedFields();");

			foreach(DistributedPropertyInfo propInfo in classInfo.Properties)
			{
				Output.AppendLine($"\t\t\t{propInfo.PropertyName}._imp_Initialize(this, {propInfo.PropertyId});");
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
			foreach(var cd in classDeclarations)
			{
				if(ExamineClassDeclaration(context, cd))
				{
					generated = true;
				}
			}

			if(generated)
			{
				string sourcePath = Path.GetDirectoryName(fileTree.FilePath);
				AddSourcePath(sourcePath);
			}
			

		}

		private bool ExamineClassDeclaration(GeneratorExecutionContext context, ClassDeclarationSyntax cd)
		{
			bool generated = false;

			var attributeLists = cd.ChildNodes().OfType<AttributeListSyntax>();
			foreach(var attributeList in attributeLists)
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

			return generated;
		}

		private void AnalyseDistributedClass(GeneratorExecutionContext context, ClassDeclarationSyntax cd)
		{
			string? classNamespace = GetNamespace(cd);

			DistributedClassInfo classInfo = new DistributedClassInfo(cd.Identifier.Text, classNamespace);

			WriteInfo("Found distributed class " + classInfo.Namespace + "." + classInfo.ClassName);

			foreach (var fieldDecl in cd.ChildNodes().OfType<FieldDeclarationSyntax>())
			{
				foreach (var attribute in fieldDecl.DescendantNodes().OfType<AttributeSyntax>())
				{
					if (attribute.Name.ToString() == "Distributed")
					{
						AnalyseDistributedField(context, fieldDecl, attribute, classInfo);
					}
				}
			}

			DistributedClasses.Add(classInfo);
		}

		private void AnalyseDistributedField(GeneratorExecutionContext context, FieldDeclarationSyntax fd, AttributeSyntax attr, DistributedClassInfo classInfo)
		{
			string? distributedPropertyId = null;

			var distributeArguments = attr.DescendantNodes().OfType<AttributeArgumentSyntax>();
			foreach (AttributeArgumentSyntax argSyntax in distributeArguments)
			{
				string? argName = null;
				if (argSyntax.NameEquals != null)
				{
					IdentifierNameSyntax argIdentifier = argSyntax.NameEquals.ChildNodes().OfType<IdentifierNameSyntax>().FirstOrDefault();
					if (argIdentifier != null)
					{
						argName = argIdentifier.Identifier.Text;
					}
				}

				string? argValue = argSyntax.Expression?.ToString();

				if (argName == null)
				{
					// propertyId
					distributedPropertyId = argValue;
				}
			}

			if (distributedPropertyId == null)
			{
				var msg = new DiagnosticDescriptor("IMP2", "Missing distributed property id", "Distributed property must have an id", "Mismatch", DiagnosticSeverity.Error, true);
				context.ReportDiagnostic(Diagnostic.Create(msg, distributeArguments.First().GetLocation()));
				return;
			}

			VariableDeclarationSyntax vd = fd.ChildNodes().OfType<VariableDeclarationSyntax>().First();
			if (vd.Variables.Count != 1) {
				var msg = new DiagnosticDescriptor("IMP2", "Multple variable declaration", "Declaring multiple variables per type not supported", "Mismatch", DiagnosticSeverity.Error, true);
				context.ReportDiagnostic(Diagnostic.Create(msg, vd.GetLocation()));

				return;
			}

			VariableDeclaratorSyntax varDef = vd.Variables.First();

			GenericNameSyntax genericField = vd.ChildNodes().OfType<GenericNameSyntax>().FirstOrDefault();
			if (genericField == null)
			{
				var msg = new DiagnosticDescriptor("IMP2", "Invalid Distributed Type", "Distributed attribute on an unsupported field type", "Mismatch", DiagnosticSeverity.Error, true);
				context.ReportDiagnostic(Diagnostic.Create(msg, vd.GetLocation()));

				return;
			}

			string fieldType = genericField.Identifier.Text;
			if (!ValidDistributedFieldTypes.Contains(fieldType))
			{
				var msg = new DiagnosticDescriptor("IMP1", "Unknown Distributed Type", "Type " + fieldType+ " is not a supported distributed field type", "Mismatch", DiagnosticSeverity.Error, true);
				context.ReportDiagnostic(Diagnostic.Create(msg, genericField.GetLocation()));
				
				return;
			}

			TypeArgumentListSyntax genericArgsList = genericField.ChildNodes().OfType<TypeArgumentListSyntax>().First();
			if (genericArgsList == null)
			{
				var msg = new DiagnosticDescriptor("IMP1", "Generic types not defined", "Type " + fieldType+ " is not a supported distributed field type", "Mismatch", DiagnosticSeverity.Error, true);
				context.ReportDiagnostic(Diagnostic.Create(msg, genericField.GetLocation()));
				
				return;
			}

			var dTypeIdentifier = genericArgsList.Arguments.First();

            DistributedPropertyInfo propInfo = new DistributedPropertyInfo(varDef.Identifier.ToString(),
                                                    fieldType, dTypeIdentifier.ToString(), distributedPropertyId);

            classInfo.Properties.Add(propInfo);

			WriteInfo("Found distributed field " + propInfo.PropertyDType + " " + propInfo.PropertyName + " (" + propInfo.PropertyId+", "+")");
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
