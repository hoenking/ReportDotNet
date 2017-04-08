using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Web.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ReportDotNet.Core;
using ReportDotNet.Playground.Template;

namespace ReportDotNet.Web.App
{
	public class ReportRenderer
	{
		private readonly DirectoryWatcher directoryWatcher;

		public ReportRenderer(DirectoryWatcher directoryWatcher)
		{
			this.directoryWatcher = directoryWatcher;
		}

		public Report Render(IDocument document)
		{
			var templateProjectDirectory = GetTemplateProjectDirectory();
			var templateDirectoryName = File.ReadAllText(Path.Combine(templateProjectDirectory, "CurrentTemplateDirectory.txt"));
			var templateDirectoryPath = Path.Combine(templateProjectDirectory, templateDirectoryName);
			EnsureTemplateDirectory(templateDirectoryPath, templateDirectoryName, templateProjectDirectory);

			var templatePath = Path.Combine(templateDirectoryPath, "Template.cs");
			var templateType = CreateTemplateType(templatePath);
			var log = new List<string>();
			Action<int, string, object> logAction = (lineNumber, line, obj) => log.Add($"#{lineNumber}: {line}: {obj}");
			var method = GetFillDocumentMethod(templateType);
			var arguments = method.GetParameters().Length == 2
								? new object[] { document, logAction }
								: new object[] { document, logAction, templateDirectoryPath };
			method.Invoke(null, arguments);
			directoryWatcher.Watch(templateProjectDirectory);
			return new Report
				   {
					   Log = log.ToArray(),
					   RenderedBytes = document.Save()
				   };
		}

		private static void EnsureTemplateDirectory(string templateDirectoryPath, string templateDirectoryName, string templateProjectDirectory)
		{
			if (!Directory.Exists(templateDirectoryPath))
			{
				var directories = new DirectoryInfo(templateProjectDirectory)
					.EnumerateDirectories()
					.Select(x => x.Name)
					.Except(new[] { "bin", "obj", "Properties" });
				throw new Exception($"Are you sure that directory {templateDirectoryName} exists in template project?" +
									$" There are only {string.Join(", ", directories)}.");
			}
		}

		private static Type CreateTemplateType(string templatePath)
		{
			var references = new[]
							 {
								 Assembly.GetExecutingAssembly(),
								 typeof(StubForNamespace).Assembly
							 }
				.SelectMany(x => x.GetReferencedAssemblies())
				.Select(x => x.FullName)
				.Distinct()
				.Select(Assembly.Load)
				.Select(a => MetadataReference.CreateFromFile(a.Location))
				.ToArray();
			var compilation = CSharpCompilation.Create(assemblyName: "NewReport.dll",
													   syntaxTrees: new[] { GetSyntaxTree(templatePath) },
													   references: references,
													   options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
			try
			{
				using (var ms = new MemoryStream())
				{
					var result = compilation.Emit(ms);
					if (result.Success)
						return Assembly.Load(ms.ToArray()).GetTypes().Single(x => !x.IsNested);

					var failures = result.Diagnostics.Where(diagnostic =>
																diagnostic.IsWarningAsError ||
																diagnostic.Severity == DiagnosticSeverity.Error);
					throw new InvalidOperationException(string.Join(Environment.NewLine, failures.Select(x => $"{x.Id}: {x.GetMessage()}")));
				}
			}
			finally
			{
				File.Delete(compilation.AssemblyName);
			}
		}

		private static string GetTemplateProjectDirectory()
		{
			var webProjectPath = HostingEnvironment.ApplicationPhysicalPath;
			var solutionPath = Path.Combine(webProjectPath, "..");
			return Path.Combine(solutionPath, typeof(StubForNamespace).Namespace);
		}

		private static MethodInfo GetFillDocumentMethod(Type type)
		{
			return type.GetMethods()
					   .Single(m =>
							   {
								   var parameters = m.GetParameters();
								   return m.IsStatic
										  && parameters.Length >= 2
										  && parameters[0].ParameterType == typeof(IDocument)
										  && parameters[1].ParameterType == typeof(Action<int, string, object>)
										  && (parameters.Length == 2 || parameters[2].ParameterType == typeof(string));
							   });
		}

		private static readonly Regex logRegex = new Regex("\\slog[(](.*)[)];", RegexOptions.Compiled | RegexOptions.Singleline);

		private static readonly Regex logParameterRegex = new Regex("\\sAction<object> log", RegexOptions.Compiled | RegexOptions.Singleline);

		private static SyntaxTree GetSyntaxTree(string fileName)
		{
			var lines = File.ReadAllLines(fileName)
							.Select((l, i) => logParameterRegex.Replace(l, "Action<int, string, object> log"))
							.Select((l, i) => logRegex.Replace(l, $"log({i + 1}, \"$1\", $1);"))
							.ToArray();
			return CSharpSyntaxTree.ParseText(string.Join(Environment.NewLine, lines));
		}

		public class Report
		{
			public byte[] RenderedBytes { get; set; }
			public string[] Log { get; set; }
		}
	}
}