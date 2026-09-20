using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Moongazing.OrionAudit.Generators;

/// <summary>
/// Emits the AOT-safe registration glue for every <c>[OrionAuditModule]</c>-decorated partial
/// class in the consuming compilation. The generator walks the same compilation for
/// <c>[Auditable]</c> types and produces, on each module:
/// <list type="bullet">
///   <item><c>RegisterAuditedTypes(AuditConfigurationBuilder)</c> — replaces the reflective scan.</item>
///   <item><c>AuditedTypeNames</c> — the names discovered, for wiring a manual JSON context.</item>
/// </list>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class OrionAuditModuleGenerator : IIncrementalGenerator
{
    private const string ModuleAttributeFqn = "Moongazing.OrionAudit.OrionAuditModuleAttribute";
    private const string AuditableAttributeFqn = "Moongazing.OrionAudit.AuditableAttribute";
    private const string HelpLink = "https://github.com/tunahanaliozturk/OrionAudit#source-generated-registration-aot-aware";

    /// <summary>
    /// OA0001: an <c>[OrionAuditModule]</c> type, or one of the types it is nested in, is not
    /// declared <c>partial</c>, so the generator has nothing it can add members to.
    /// </summary>
    internal static readonly DiagnosticDescriptor ModuleNotPartial = new DiagnosticDescriptor(
        id: "OA0001",
        title: "[OrionAuditModule] type is not partial",
        messageFormat: "'{0}' carries [OrionAuditModule] but '{1}' is not declared 'partial', so no RegisterAuditedTypes method is generated",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The generator adds RegisterAuditedTypes and AuditedTypeNames to the annotated type through a second partial declaration. The annotated type and every type it is nested in must therefore be declared 'partial'.",
        helpLinkUri: HelpLink);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var modules = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ModuleAttributeFqn,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, _) => (INamedTypeSymbol?)ctx.TargetSymbol)
            .Where(static sym => sym is not null)
            .Select(static (sym, _) => sym!)
            .Collect();

        var auditableTypes = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AuditableAttributeFqn,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, _) => (INamedTypeSymbol?)ctx.TargetSymbol)
            .Where(static sym => sym is not null
                                 && !sym.IsAbstract
                                 // The generated registration call uses the type via typeof(...) from the
                                 // emitted partial class. That requires the type to be reachable from
                                 // somewhere in the compilation — private/protected nested types
                                 // declared inside test classes etc. would not be. Skip them; the
                                 // reflective AuditConfigurationBuilder.Audit<T>() path still works for
                                 // those cases (with a trim warning).
                                 && IsReachable(sym!))
            .Select(static (sym, _) => sym!)
            .Collect();

        context.RegisterSourceOutput(modules.Combine(auditableTypes), Emit);
    }

    private static bool IsReachable(INamedTypeSymbol type)
    {
        // Walk outward through nested types — every container must be at least Internal.
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public
                && current.DeclaredAccessibility != Accessibility.Internal)
            {
                return false;
            }
        }
        return true;
    }

    private static void Emit(
        SourceProductionContext spc,
        (ImmutableArray<INamedTypeSymbol> Modules, ImmutableArray<INamedTypeSymbol> Types) input)
    {
        foreach (var module in input.Modules)
        {
            // Outermost first: every enclosing type has to be re-declared around the module, or the
            // emitted members land on an unrelated top-level type that happens to share its name and
            // the consumer's Outer.Module.RegisterAuditedTypes(...) call does not resolve.
            var chain = new List<INamedTypeSymbol>();
            for (var current = module; current is not null; current = current.ContainingType)
            {
                chain.Add(current);
            }

            chain.Reverse();

            var declarations = new List<TypeDeclarationSyntax>(chain.Count);
            INamedTypeSymbol? blocker = null;
            foreach (var link in chain)
            {
                var declaration = FirstDeclaration(link);
                if (declaration is null || !declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
                {
                    blocker = link;
                    break;
                }

                declarations.Add(declaration);
            }

            // Emitting a second, non-matching declaration would only turn a clear miss into an
            // opaque CS0260 on the consumer's own type. Say what is wrong instead.
            if (blocker is not null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    ModuleNotPartial,
                    DeclarationLocation(module),
                    module.ToDisplayString(),
                    blocker.ToDisplayString()));
                continue;
            }

            var source = EmitModule(module, chain, declarations, input.Types);
            var hint = $"{module.ContainingNamespace.ToDisplayString().Replace('.', '_')}_{module.Name}.OrionAuditModule.g.cs";
            spc.AddSource(hint, source);
        }
    }

    private static TypeDeclarationSyntax? FirstDeclaration(INamedTypeSymbol type)
    {
        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is TypeDeclarationSyntax declaration)
            {
                return declaration;
            }
        }

        return null;
    }

    private static Location DeclarationLocation(INamedTypeSymbol type) =>
        type.Locations.FirstOrDefault(static l => l.IsInSource) ?? Location.None;

    private static string EmitModule(
        INamedTypeSymbol module,
        List<INamedTypeSymbol> chain,
        List<TypeDeclarationSyntax> declarations,
        ImmutableArray<INamedTypeSymbol> types)
    {
        var ns = module.ContainingNamespace.IsGlobalNamespace
            ? null
            : module.ContainingNamespace.ToDisplayString();

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using Moongazing.OrionAudit;");
        sb.AppendLine("using Moongazing.OrionAudit.Configuration;");
        sb.AppendLine();

        if (ns is not null)
        {
            sb.Append("namespace ").Append(ns).AppendLine(";");
            sb.AppendLine();
        }

        for (var i = 0; i < chain.Count; i++)
        {
            var indent = new string(' ', i * 4);
            sb.Append(indent).AppendLine(Header(chain[i], declarations[i]));
            sb.Append(indent).AppendLine("{");
        }

        var body = new string(' ', chain.Count * 4);
        sb.Append(body).AppendLine("/// <summary>Registers every <c>[Auditable]</c> type discovered at compile time on the supplied builder. Source-generated; no runtime reflection.</summary>");
        sb.Append(body).AppendLine("public static void RegisterAuditedTypes(AuditConfigurationBuilder builder)");
        sb.Append(body).AppendLine("{");
        sb.Append(body).AppendLine("    if (builder is null)");
        sb.Append(body).AppendLine("    {");
        sb.Append(body).AppendLine("        throw new global::System.ArgumentNullException(nameof(builder));");
        sb.Append(body).AppendLine("    }");
        foreach (var t in types)
        {
            // FullyQualifiedFormat already emits "global::Namespace.Type" — pass it straight through.
            sb.Append(body).Append("    builder.Audit(typeof(")
              .Append(t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
              .AppendLine("));");
        }

        sb.Append(body).AppendLine("}");
        sb.AppendLine();
        sb.Append(body).AppendLine("/// <summary>");
        sb.Append(body).AppendLine("/// The fully-qualified names of every <c>[Auditable]</c> type the generator discovered.");
        sb.Append(body).AppendLine("/// Useful as a sanity check or to wire a manual <c>JsonSerializerContext</c> (see");
        sb.Append(body).AppendLine("/// <c>OrionAuditOptions.UseJsonContext</c>): each name here should have a matching");
        sb.Append(body).AppendLine("/// <c>[JsonSerializable(typeof(...))]</c> attribute on the consumer's context.");
        sb.Append(body).AppendLine("/// </summary>");
        sb.Append(body).AppendLine("public static global::System.Collections.Generic.IReadOnlyList<string> AuditedTypeNames { get; } = new string[]");
        sb.Append(body).AppendLine("{");
        foreach (var t in types)
        {
            sb.Append(body).Append("    \"")
              .Append(t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
              .AppendLine("\",");
        }

        sb.Append(body).AppendLine("};");

        for (var i = chain.Count - 1; i >= 0; i--)
        {
            sb.Append(new string(' ', i * 4)).AppendLine("}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Re-declares one link of the nesting chain as the consumer wrote it: same accessibility
    /// (partial declarations must agree), same type parameters, same constraints. A generic module
    /// emitted without its type parameter list is a different type of arity 0, not a part of it.
    /// </summary>
    private static string Header(INamedTypeSymbol type, TypeDeclarationSyntax declaration)
    {
        var sb = new StringBuilder();
        sb.Append(AccessModifier(type)).Append(" partial class ").Append(type.Name);

        if (declaration.TypeParameterList is not null)
        {
            sb.Append(declaration.TypeParameterList.WithoutTrivia().ToFullString());
        }

        // Constraints may be written on any one part of a partial type; the others must repeat them
        // identically or omit them. Copying verbatim from whichever part carries them is valid
        // either way, and the type parameter list always travels with them.
        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is TypeDeclarationSyntax other && other.ConstraintClauses.Count > 0)
            {
                foreach (var clause in other.ConstraintClauses)
                {
                    sb.Append(' ').Append(clause.WithoutTrivia().ToFullString().Trim());
                }

                break;
            }
        }

        return sb.ToString();
    }

    private static string AccessModifier(INamedTypeSymbol type) => type.DeclaredAccessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Private => "private",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        _ => "internal",
    };
}
