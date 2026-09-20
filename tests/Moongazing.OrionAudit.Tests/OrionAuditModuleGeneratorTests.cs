using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Moongazing.OrionAudit.Configuration;
using Moongazing.OrionAudit.Generators;

namespace Moongazing.OrionAudit.Tests;

/// <summary>
/// Drives <see cref="OrionAuditModuleGenerator"/> over hand-written consumer sources. Where the
/// defect is emitted code that does not compile, the assertion is that the consumer compilation has
/// no errors — "the generator produced something" would pass on every one of these.
/// <see cref="SourceGenSmokeTests"/> covers the happy path from the other side, by compiling this
/// test project itself with the generator attached.
/// </summary>
public class OrionAuditModuleGeneratorTests
{
    [Fact]
    public void NestedModule_EmitsInsideItsContainingType()
    {
        const string source = """
            using Moongazing.OrionAudit;
            using Moongazing.OrionAudit.Configuration;

            namespace Consumer;

            [Auditable]
            public sealed class Widget { public int Id { get; set; } }

            public partial class Startup
            {
                [OrionAuditModule]
                public partial class Registry { }
            }

            public static class Consume
            {
                public static void Use(AuditConfigurationBuilder builder) =>
                    Startup.Registry.RegisterAuditedTypes(builder);
            }
            """;

        var run = RunAndAssertConsumerCompiles(source);

        // The whole containing chain has to be re-declared, or the member lands on an unrelated
        // top-level Registry and Startup.Registry.RegisterAuditedTypes does not exist.
        Assert.Contains("partial class Startup", SingleSource(run), StringComparison.Ordinal);
    }

    [Fact]
    public void NonPartialModule_ReportsOA0001AtTheModuleDeclaration()
    {
        const string source = """
            using Moongazing.OrionAudit;

            namespace Consumer;

            [OrionAuditModule]
            public class Registry { }
            """;

        var diagnostic = SingleDiagnostic(Run(source).Run, "OA0001");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("Registry", TextAt(source, diagnostic));
    }

    [Fact]
    public void ModuleNestedInNonPartialType_ReportsOA0001NamingTheContainer()
    {
        const string source = """
            using Moongazing.OrionAudit;

            namespace Consumer;

            public class Startup
            {
                [OrionAuditModule]
                public partial class Registry { }
            }
            """;

        var diagnostic = SingleDiagnostic(Run(source).Run, "OA0001");

        Assert.Contains("Consumer.Startup", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.Equal("Registry", TextAt(source, diagnostic));
    }

    private static (Compilation Output, GeneratorDriverRunResult Run) Run(string source)
    {
        // Every assembly the test host has loaded, plus OrionAudit itself: enough for a consumer
        // source that uses the attributes and AuditConfigurationBuilder.
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(static a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(static a => a.Location)
            .Append(typeof(AuditConfigurationBuilder).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();

        var compilation = CSharpCompilation.Create(
            "OrionAuditGeneratorTest_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new OrionAuditModuleGenerator().AsSourceGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        return (output, driver.GetRunResult());
    }

    private static GeneratorDriverRunResult RunAndAssertConsumerCompiles(string source)
    {
        var (output, run) = Run(source);

        Assert.All(run.Results, static result => Assert.Null(result.Exception));

        var errors = output.GetDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.True(
            errors.Count == 0,
            "The consumer did not compile against the generated source:\n"
            + string.Join("\n", errors)
            + "\n\n--- Generated ---\n"
            + string.Join("\n\n", run.Results.SelectMany(r => r.GeneratedSources).Select(s => s.SourceText.ToString())));

        return run;
    }

    private static string SingleSource(GeneratorDriverRunResult run) =>
        Assert.Single(run.Results.SelectMany(r => r.GeneratedSources)).SourceText.ToString();

    private static Diagnostic SingleDiagnostic(GeneratorDriverRunResult run, string id) =>
        Assert.Single(run.Diagnostics.Where(d => d.Id == id));

    /// <summary>The source text the diagnostic points at — a symbol's location is its identifier.</summary>
    private static string TextAt(string source, Diagnostic diagnostic) =>
        source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length);
}
