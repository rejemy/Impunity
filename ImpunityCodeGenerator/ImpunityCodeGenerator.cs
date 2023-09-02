using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.IO;
using System;

namespace SourceGenerator
{

	public class DistributedPropertyInfo
	{
		public string PropertyName { get; set; }
		public string PropertyDType { get; set; }
		public string PropertyId { get; set; }
		public string OnChangedMethodName { get; set; }

		public DistributedPropertyInfo(string name, string dtype, string propId)
		{
			PropertyName = name;
			PropertyDType = dtype;
			PropertyId = propId;
		}
	}

	public class DistributedClassInfo
	{
		public string ClassName { get; set; }
		public string Namespace { get; set; }
		public List<DistributedPropertyInfo> Properties { get; set; }

		public DistributedClassInfo(string name, string nspace)
		{
			ClassName = name;
			Namespace = nspace;
			Properties = new List<DistributedPropertyInfo>();
		}
	}

	[Generator]
	public class DistributedEntityGenerator : ISourceGenerator
	{
		public static StringBuilder Output = new StringBuilder();
		public static StringBuilder Info = new StringBuilder();

		public List<DistributedClassInfo> DistributedClasses;
		public string SourceBasePath;

		public void Initialize(GeneratorInitializationContext context)
		{
			//WriteLine("Running Initialize");
		}

		public static void WriteLine(string line)
		{
			Output.AppendLine("/* " + line + "*/");
		}

		private void LogNode(SyntaxNode node, string indent)
		{
			WriteLine(indent + "Node: " + node.GetType().Name);
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
			//WriteLine("Running Execute");
			DistributedClasses = new List<DistributedClassInfo>();
			SourceBasePath = null;
			Output.Clear();
			//Info.Clear();

			WriteInfo("Running codegen 3 against " + context.Compilation.AssemblyName + " at " + DateTime.Now.ToString());
			
			foreach (var syntaxTree in context.Compilation.SyntaxTrees)
			{
				ExamineSyntaxTree(syntaxTree);
				//WriteLine(syntaxTree.FilePath);
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

			//var msg2 = new DiagnosticDescriptor("test2", "Compilation complete error", "Compiled " + DistributedClasses.Count + " classes", "Category", DiagnosticSeverity.Error, true);
			//context.ReportDiagnostic(Diagnostic.Create(msg2, Location.None));

			//WriteInfo();

			if (DistributedClasses.Count > 0)
			{
				context.AddSource("ImpunityCode.generated.cs", Output.ToString());
			}

		}

		private void WriteInfo()
		{
			if (SourceBasePath == null)
			{
				SourceBasePath = Directory.GetCurrentDirectory();
			}

			string infoFilename = Path.Combine(SourceBasePath, "ImpunityGenInfo.txt");

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

			

			Output.AppendLine("using System.IO;");
			Output.AppendLine("using Impunity.GameState;\n");
			
			string currentNamespace = null;
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

				Output.AppendLine("\tpublic partial class " + classInfo.ClassName + "\n\t{");

				foreach(DistributedPropertyInfo propInfo in classInfo.Properties)
				{
					string propSource = $@"		public void Set{propInfo.PropertyName}({propInfo.PropertyDType} v)
		{{
			if ({propInfo.PropertyName}.Set(v)) SetDirty({propInfo.PropertyId});
		}}
		public {propInfo.PropertyDType} Get{propInfo.PropertyName}()
		{{
			return ({propInfo.PropertyDType}){propInfo.PropertyName};
		}}
		private void imp_Write{propInfo.PropertyName}(BinaryWriter w)
		{{
			{propInfo.PropertyName}.WriteChangesTo(w);
		}}";
					Output.AppendLine(propSource);
					string updateMethodSource = null;
					if(propInfo.OnChangedMethodName != null)
					{
						updateMethodSource = $@"		private void imp_Update{propInfo.PropertyName}(BinaryReader r)
		{{
			{propInfo.PropertyDType} oldValue = {propInfo.PropertyName};
			{propInfo.PropertyName}.ReadChangesFrom(r);
			{propInfo.PropertyDType} newValue = {propInfo.PropertyName};
			{propInfo.OnChangedMethodName}(oldValue, newValue);
		}}";
					}
					else
					{
						updateMethodSource = $@"        private void imp_Update{propInfo.PropertyName}(BinaryReader r)
		{{
			{propInfo.PropertyName}.ReadChangesFrom(r);
		}}";
					}
					Output.AppendLine(updateMethodSource);
				}

				Output.AppendLine("\t}\n");
			}

			if (currentNamespace != null)
			{
				Output.AppendLine("}\n");
			}
		}

		private void ExamineSyntaxTree(SyntaxTree fileTree)
		{
			bool generated = false;

			var classDeclarations = fileTree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>();
			foreach(var cd in classDeclarations)
			{
				if(ExamineClassDeclaration(cd))
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

		private bool ExamineClassDeclaration(ClassDeclarationSyntax cd)
		{
			bool generated = false;

			var attributeLists = cd.ChildNodes().OfType<AttributeListSyntax>();
			foreach(var attributeList in attributeLists)
			{
				foreach (AttributeSyntax attribute in attributeList.ChildNodes())
				{
					if (attribute.Name.ToString() == "DistributedEntity")
					{
						AnalyseDistributedClass(cd);
						generated = true;
					}
				}
			}

			return generated;
		}

		private void AnalyseDistributedClass(ClassDeclarationSyntax cd)
		{
			string classNamespace = GetNamespace(cd);

			DistributedClassInfo classInfo = new DistributedClassInfo(cd.Identifier.Text, classNamespace);

			WriteInfo("Found distributed class " + classInfo.Namespace + "." + classInfo.ClassName);

			foreach (var fieldDecl in cd.ChildNodes().OfType<FieldDeclarationSyntax>())
			{
				foreach (var attribute in fieldDecl.DescendantNodes().OfType<AttributeSyntax>())
				{
					if (attribute.Name.ToString() == "Distributed")
					{
						AnalyseDistributedField(fieldDecl, attribute, classInfo);
					}
				}
			}

			DistributedClasses.Add(classInfo);
		}

		private void AnalyseDistributedField(FieldDeclarationSyntax fd, AttributeSyntax attr, DistributedClassInfo classInfo)
		{
			string distributedPropertyId = null;
			string onChangedMethodName = null;

			AttributeArgumentListSyntax distributeArguments = attr.ChildNodes().OfType<AttributeArgumentListSyntax>().First();
			foreach (AttributeArgumentSyntax argSyntax in distributeArguments.Arguments)
			{
				string argName = argSyntax.NameEquals?.ToString();
				string argValue = argSyntax.Expression?.ToString();

				if (argName == null)
				{
					// propertyId
					distributedPropertyId = argValue;
				}
				else if(argName == "OnChanged")
				{
					onChangedMethodName = argValue;
				}
			}

			VariableDeclarationSyntax vd = fd.ChildNodes().OfType<VariableDeclarationSyntax>().First();
			VariableDeclaratorSyntax varDef = vd.Variables.First();

			GenericNameSyntax genericField = vd.ChildNodes().OfType<GenericNameSyntax>().FirstOrDefault();
			if (genericField == null)
			{
				return;
			}

			var dTypeIdentifier = genericField.DescendantNodes().OfType<IdentifierNameSyntax>().First();

			DistributedPropertyInfo propInfo = new DistributedPropertyInfo(varDef.Identifier.ToString(), dTypeIdentifier.ToString(), distributedPropertyId);
			propInfo.OnChangedMethodName = onChangedMethodName;

			classInfo.Properties.Add(propInfo);

			WriteInfo("Found distributed field " + propInfo.PropertyDType + " " + propInfo.PropertyName + " (" + propInfo.PropertyId+")");
		}

		// determine the namespace the class/enum/struct is declared in, if any
		static string GetNamespace(BaseTypeDeclarationSyntax syntax)
		{
			// If we don't have a namespace at all we'll return an empty string
			// This accounts for the "default namespace" case
			string nameSpace = null;

			// Get the containing syntax node for the type declaration
			// (could be a nested type, for example)
			SyntaxNode potentialNamespaceParent = syntax.Parent;

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
