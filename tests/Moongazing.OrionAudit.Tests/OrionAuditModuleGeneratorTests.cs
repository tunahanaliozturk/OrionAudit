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

    [Fact]
    public void GenericModule_EmitsItsTypeParametersAndConstraints()
    {
        const string source = """
            using Moongazing.OrionAudit;
            using Moongazing.OrionAudit.Configuration;

            namespace Consumer;

            [Auditable]
            public sealed class Widget { public int Id { get; set; } }

            [OrionAuditModule]
            public partial class Registry<TMarker> where TMarker : class, new() { }

            public static class Consume
            {
                public static void Use(AuditConfigurationBuilder builder) =>
                    Registry<Widget>.RegisterAuditedTypes(builder);
            }
            """;

        var generated = SingleSource(RunAndAssertConsumerCompiles(source));

        // Dropping the type parameters emitted a second, arity-0 'Registry' instead of a part of
        // Registry<TMarker>; dropping the constraints is CS0265 against the part that has them.
        Assert.Contains("partial class Registry<TMarker>", generated, StringComparison.Ordinal);
        Assert.Contains("where TMarker : class, new()", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericContainingType_IsReDeclaredWithItsTypeParameters()
    {
        const string source = """
            using Moongazing.OrionAudit;
            using Moongazing.OrionAudit.Configuration;

            namespace Consumer;

            public partial class Startup<TApp> where TApp : notnull
            {
                [OrionAuditModule]
                public partial class Registry { }
            }

            public static class Consume
            {
                public static void Use(AuditConfigurationBuilder builder) =>
                    Startup<string>.Registry.RegisterAuditedTypes(builder);
            }
            """;

        var generated = SingleSource(RunAndAssertConsumerCompiles(source));

        Assert.Contains("partial class Startup<TApp>", generated, StringComparison.Ordinal);
        Assert.Contains("where TApp : notnull", generated, StringComparison.Ordinal);
    }

    // [OrionAuditModule] and [Auditable] are AttributeTargets.Class, which covers a record class but
    // not a struct or record struct, so those are rejected before the generator ever sees them.
    [Theory]
    [InlineData("class")]
    [InlineData("record")]
    [InlineData("record class")]
    public void ModuleDeclaredWithAnyClassKeyword_IsGeneratedWithThatKeyword(string keyword)
    {
        var source = $$"""
            using Moongazing.OrionAudit;
            using Moongazing.OrionAudit.Configuration;

            namespace Consumer;

            [Auditable]
            public sealed {{keyword}} Widget { public int Id { get; set; } }

            [OrionAuditModule]
            public partial {{keyword}} Registry { }

            public static class Consume
            {
                public static void Use(AuditConfigurationBuilder builder) =>
                    Registry.RegisterAuditedTypes(builder);
            }
            """;

        var generated = SingleSource(RunAndAssertConsumerCompiles(source));

        // A record is a RecordDeclarationSyntax, so the old 'node is ClassDeclarationSyntax'
        // predicate dropped it entirely - no module, no registration, no word about either.
        Assert.Contains($"partial {keyword} Registry", generated, StringComparison.Ordinal);
        Assert.Contains("typeof(global::Consumer.Widget)", generated, StringComparison.Ordinal);
    }

    [Theory]
    // namespace A.B + class C_D vs namespace A.B.C + class D: both used to escape to "A_B_C_D".
    [InlineData("namespace A.B { [OrionAuditModule] public partial class C_D { } }",
                "namespace A.B.C { [OrionAuditModule] public partial class D { } }")]
    // namespace A + class B_C vs namespace A.B + class C: "A_B_C" again.
    [InlineData("namespace A { [OrionAuditModule] public partial class B_C { } }",
                "namespace A.B { [OrionAuditModule] public partial class C { } }")]
    // A nested module took only its namespace, so A.B.C nested in B collided with a top-level A.C.
    [InlineData("namespace A { public partial class B { [OrionAuditModule] public partial class C { } } }",
                "namespace A { [OrionAuditModule] public partial class C { } }")]
    // Module vs Module<T>: the hint has to carry the arity.
    [InlineData("namespace A { [OrionAuditModule] public partial class Module { } }",
                "namespace A { [OrionAuditModule] public partial class Module<T> { } }")]
    public void ModulesWithDistinctNames_NeverShareAHintName(string first, string second)
    {
        var source = "using Moongazing.OrionAudit;\n" + first + "\n" + second + "\n";

        // A duplicate hint name makes AddSource throw ArgumentException, which fails the whole
        // generator run and drops every file it had already produced.
        var run = RunAndAssertConsumerCompiles(source);
        var result = Assert.Single(run.Results);

        Assert.Equal(2, result.GeneratedSources.Length);
        Assert.Equal(
            2,
            result.GeneratedSources.Select(s => s.HintName).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void UnreachableAuditableType_ReportsOA0003AtItsDeclaration()
    {
        const string source = """
            using Moongazing.OrionAudit;

            namespace Consumer;

            [OrionAuditModule]
            public partial class Registry { }

            public class Host
            {
                [Auditable]
                private sealed class Hidden { public int Id { get; set; } }
            }
            """;

        var (_, run) = Run(source);

        // Dropped in silence before: the consumer believes Hidden is audited and only finds out
        // when no audit row is ever written for it.
        var diagnostic = SingleDiagnostic(run, "OA0003");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("Hidden", TextAt(source, diagnostic));
        Assert.DoesNotContain("Hidden", SingleSource(run), StringComparison.Ordinal);
    }

    [Fact]
    public void AbstractAuditableType_ReportsOA0002AtItsDeclaration()
    {
        const string source = """
            using Moongazing.OrionAudit;

            namespace Consumer;

            [OrionAuditModule]
            public partial class Registry { }

            [Auditable]
            public abstract class EntityBase { public int Id { get; set; } }
            """;

        var (_, run) = Run(source);

        var diagnostic = SingleDiagnostic(run, "OA0002");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("EntityBase", TextAt(source, diagnostic));
    }

    [Fact]
    public void ReachableAuditableTypes_AreRegisteredWithoutADiagnostic()
    {
        const string source = """
            using Moongazing.OrionAudit;

            namespace Consumer;

            [OrionAuditModule]
            public partial class Registry { }

            [Auditable]
            public sealed class Widget { public int Id { get; set; } }

            internal partial class Host
            {
                [Auditable]
                internal sealed class Nested { public int Id { get; set; } }
            }
            """;

        var run = RunAndAssertConsumerCompiles(source);

        Assert.Empty(run.Diagnostics);
        Assert.Contains("typeof(global::Consumer.Widget)", SingleSource(run), StringComparison.Ordinal);
        Assert.Contains("typeof(global::Consumer.Host.Nested)", SingleSource(run), StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAnyModule_AnUnregisterableAuditableTypeIsNotReported()
    {
        const string source = """
            using Moongazing.OrionAudit;

            namespace Consumer;

            public class Host
            {
                [Auditable]
                private sealed class Hidden { public int Id { get; set; } }
            }
            """;

        // Nothing is generated at all, so the consumer is on the reflective path and there is
        // nothing to warn about. TreatWarningsAsErrors makes a spurious warning a build break.
        Assert.Empty(Run(source).Run.Diagnostics);
    }

    [Theory]
    // The constraint type is only in scope through a using directive of the consumer's file...
    [InlineData("using Contracts;", "IMarker")]
    // ...or only through an alias, which does not exist anywhere else in the compilation.
    [InlineData("using Marker = Contracts.IMarker;", "Marker")]
    public void ConstraintTypeReachableOnlyThroughAUsing_IsEmittedFullyQualified(
        string usingDirective,
        string constraintName)
    {
        var source = $$"""
            namespace Contracts
            {
                public interface IMarker { }
            }

            namespace Consumer
            {
                using Moongazing.OrionAudit;
                using Moongazing.OrionAudit.Configuration;
                {{usingDirective}}

                [OrionAuditModule]
                public partial class Registry<T> where T : {{constraintName}} { }

                public sealed class Marked : Contracts.IMarker { }

                public static class Consume
                {
                    public static void Use(AuditConfigurationBuilder builder) =>
                        Registry<Marked>.RegisterAuditedTypes(builder);
                }
            }
            """;

        // The generated file carries none of the consumer's using directives, so a constraint
        // copied verbatim from the declaration is CS0246 there - and then CS0265 against the part
        // that does resolve it.
        var generated = SingleSource(RunAndAssertConsumerCompiles(source));

        Assert.Contains("where T : global::Contracts.IMarker", generated, StringComparison.Ordinal);
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
