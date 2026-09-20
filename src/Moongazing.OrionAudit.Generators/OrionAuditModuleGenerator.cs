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
/// type in the consuming compilation. The generator walks the same compilation for
/// <c>[Auditable]</c> types and produces, on each module:
/// <list type="bullet">
///   <item><c>RegisterAuditedTypes(AuditConfigurationBuilder)</c> — replaces the reflective scan.</item>
///   <item><c>AuditedTypeNames</c> — the names discovered, for wiring a manual JSON context.</item>
/// </list>
/// Anything the generator cannot register is reported as a diagnostic (OA0001-OA0003) rather than
/// dropped in silence: a consumer who asked for a type to be audited must never have to discover
/// from a missing method or a missing audit row that the generator skipped it.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class OrionAuditModuleGenerator : IIncrementalGenerator
{
    private const string ModuleAttributeFqn = "Moongazing.OrionAudit.OrionAuditModuleAttribute";
    private const string AuditableAttributeFqn = "Moongazing.OrionAudit.AuditableAttribute";
    private const string HelpLink = "https://github.com/tunahanaliozturk/OrionAudit#source-generated-registration-aot-aware";

    /// <summary>
    /// Constraint types are emitted into a file with no using directives, so they are written
    /// <c>global::</c>-qualified, with the nullable annotation the consumer declared.
    /// </summary>
    private static readonly SymbolDisplayFormat ConstraintTypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

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

    /// <summary>
    /// OA0002: an <c>[Auditable]</c> type is abstract. Capture matches on the runtime CLR type of a
    /// tracked entity, which is never an abstract type, so registering it would audit nothing.
    /// </summary>
    internal static readonly DiagnosticDescriptor AuditableTypeIsAbstract = new DiagnosticDescriptor(
        id: "OA0002",
        title: "[Auditable] type is abstract and is not registered",
        messageFormat: "'{0}' carries [Auditable] but is abstract, so the generated module does not register it; mark the concrete derived types instead",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Audit capture matches on the runtime CLR type of a tracked entity, which is never an abstract type. Move [Auditable] onto the concrete derived types, or register the base explicitly with AuditConfigurationBuilder.Audit.",
        helpLinkUri: HelpLink);

    /// <summary>
    /// OA0003: an <c>[Auditable]</c> type is not reachable from the generated module, because it or
    /// one of the types it is nested in is more restricted than <c>internal</c>.
    /// </summary>
    internal static readonly DiagnosticDescriptor AuditableTypeNotReachable = new DiagnosticDescriptor(
        id: "OA0003",
        title: "[Auditable] type is not reachable from the generated module and is not registered",
        messageFormat: "'{0}' carries [Auditable] but '{1}' is not public or internal, so the generated module cannot name it and does not register it",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The generated registration uses typeof(...) from the module's own declaration, which can only name a type whose whole containing chain is public or internal. Widen the accessibility, or register the type at runtime with AuditConfigurationBuilder.Audit (which carries a trim warning).",
        helpLinkUri: HelpLink);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // A module or an entity may be declared as a class or a record; a record is a
        // RecordDeclarationSyntax, which 'node is ClassDeclarationSyntax' dropped without a word.
        var modules = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ModuleAttributeFqn,
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, _) => (INamedTypeSymbol?)ctx.TargetSymbol)
            .Where(static sym => sym is not null)
            .Select(static (sym, _) => sym!)
            .Collect();

        var auditableTypes = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AuditableAttributeFqn,
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, _) => (INamedTypeSymbol?)ctx.TargetSymbol)
            .Where(static sym => sym is not null)
            .Select(static (sym, _) => sym!)
            .Collect();

        context.RegisterSourceOutput(modules.Combine(auditableTypes), Emit);
    }

    /// <summary>
    /// The generated registration names the type via <c>typeof(...)</c> from the module's own
    /// declaration, so the type and every type it is nested in must be nameable from an unrelated
    /// type in this compilation. Returns the link that fails, or <see langword="null"/> when the
    /// whole chain is reachable.
    /// </summary>
    private static INamedTypeSymbol? FirstUnreachable(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (!IsNameableFromThisCompilation(current.DeclaredAccessibility))
            {
                return current;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether an unrelated type in the same compilation — which is all the generated module is —
    /// can name something with this accessibility. These diagnostics are the first this library
    /// emits, so a false positive here is a build break for a consumer with
    /// <c>TreatWarningsAsErrors</c> on a type that was always fine; it costs more than the silent
    /// drop it replaced. Every member is answered on purpose.
    /// </summary>
    private static bool IsNameableFromThisCompilation(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => true,
        Accessibility.Internal => true,

        // 'protected internal' is protected OR internal. The internal half alone lets every type in
        // this compilation name it, so the module can, whether or not it derives from the container.
        Accessibility.ProtectedOrInternal => true,

        // 'private protected' is protected AND internal. Same assembly is not enough — the caller
        // must also derive from the container, and the generated module never does.
        Accessibility.ProtectedAndInternal => false,

        // Only a derived type can name it, and the module is not one.
        Accessibility.Protected => false,
        Accessibility.Private => false,

        // NotApplicable, and anything a later language version adds. A source type declaration does
        // not produce it, so this is unreachable in practice; if it ever is reached, emitting
        // typeof(...) and letting the compiler object is a louder, more accurate failure than a
        // warning we cannot justify.
        _ => true,
    };

    private static void Emit(
        SourceProductionContext spc,
        (ImmutableArray<INamedTypeSymbol> Modules, ImmutableArray<INamedTypeSymbol> Types) input)
    {
        // Without a module nothing is generated at all, so an unregisterable [Auditable] type is not
        // yet a problem — the consumer is still on the reflective path. Stay quiet.
        if (input.Modules.IsDefaultOrEmpty)
        {
            return;
        }

        // Anything dropped here is a type the consumer asked to audit. Say so; a missing audit row
        // months later is a far worse way to find out.
        var types = new List<INamedTypeSymbol>(input.Types.Length);
        foreach (var type in input.Types)
        {
            if (type.IsAbstract)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    AuditableTypeIsAbstract, DeclarationLocation(type), type.ToDisplayString()));
                continue;
            }

            var unreachable = FirstUnreachable(type);
            if (unreachable is not null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    AuditableTypeNotReachable,
                    DeclarationLocation(type),
                    type.ToDisplayString(),
                    unreachable.ToDisplayString()));
                continue;
            }

            types.Add(type);
        }

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

            spc.AddSource(
                HintNames.ForType(module) + ".OrionAuditModule.g.cs",
                EmitModule(module, chain, declarations, types));
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
        List<INamedTypeSymbol> types)
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
    /// Re-declares one link of the nesting chain: same accessibility (partial declarations must
    /// agree), same type parameters, same constraints. A generic link emitted without its type
    /// parameter list is a different type of arity 0, not a part of it.
    /// <para>
    /// Everything but the type keyword comes from the symbol, never from the declaration's syntax.
    /// The generated file carries none of the consumer's using directives, so syntax copied out of
    /// a file that had them — <c>where T : IMarker</c>, or an alias — does not resolve there
    /// (CS0246), and the part that fails to resolve then disagrees with the one that does (CS0265).
    /// </para>
    /// </summary>
    private static string Header(INamedTypeSymbol type, TypeDeclarationSyntax declaration)
    {
        var sb = new StringBuilder();
        sb.Append(AccessModifier(type)).Append(" partial ").Append(Keyword(declaration)).Append(' ').Append(type.Name);

        AppendTypeParameters(sb, type);
        AppendConstraints(sb, type);

        return sb.ToString();
    }

    private static void AppendTypeParameters(StringBuilder sb, INamedTypeSymbol type)
    {
        if (type.TypeParameters.Length == 0)
        {
            return;
        }

        sb.Append('<');
        for (var i = 0; i < type.TypeParameters.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            var parameter = type.TypeParameters[i];
            switch (parameter.Variance)
            {
                case VarianceKind.In:
                    sb.Append("in ");
                    break;
                case VarianceKind.Out:
                    sb.Append("out ");
                    break;
                default:
                    break;
            }

            // Attributes on a type parameter are deliberately not repeated: a partial type combines
            // them from every part, so omitting them here is both legal and one less name to resolve.
            sb.Append(parameter.Name);
        }

        sb.Append('>');
    }

    /// <summary>
    /// Rebuilds the constraint clauses from the type parameter symbols, with every constraint type
    /// fully qualified. C# fixes the order: the primary constraint, then types, then <c>new()</c>.
    /// </summary>
    // ponytail: 'allows ref struct' (C# 13) has no ITypeParameterSymbol API in the Roslyn 4.10 this
    // component compiles against, so it is not reproduced. Lift the pin to surface it.
    private static void AppendConstraints(StringBuilder sb, INamedTypeSymbol type)
    {
        foreach (var parameter in type.TypeParameters)
        {
            var constraints = new List<string>();

            if (parameter.HasUnmanagedTypeConstraint)
            {
                // Roslyn sets HasValueTypeConstraint too; 'unmanaged' is the one that was written.
                constraints.Add("unmanaged");
            }
            else if (parameter.HasValueTypeConstraint)
            {
                constraints.Add("struct");
            }
            else if (parameter.HasReferenceTypeConstraint)
            {
                constraints.Add(parameter.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated
                    ? "class?"
                    : "class");
            }
            else if (parameter.HasNotNullConstraint)
            {
                constraints.Add("notnull");
            }

            for (var i = 0; i < parameter.ConstraintTypes.Length; i++)
            {
                var constraintType = parameter.ConstraintTypes[i].ToDisplayString(ConstraintTypeFormat);
                if (parameter.ConstraintNullableAnnotations[i] == NullableAnnotation.Annotated
                    && !constraintType.EndsWith("?", System.StringComparison.Ordinal))
                {
                    // A nullability mismatch between partial declarations is CS8665, which a
                    // consumer building with TreatWarningsAsErrors reads as a build break.
                    constraintType += "?";
                }

                constraints.Add(constraintType);
            }

            if (parameter.HasConstructorConstraint)
            {
                constraints.Add("new()");
            }

            if (constraints.Count > 0)
            {
                sb.Append(" where ").Append(parameter.Name).Append(" : ").Append(string.Join(", ", constraints));
            }
        }
    }

    /// <summary>
    /// The declaration's own type keyword — <c>class</c>, <c>record</c>, <c>record class</c>,
    /// <c>struct</c>, <c>record struct</c>. Hard-coding <c>class</c> makes the emitted part
    /// disagree with the declaration it is supposed to join.
    /// </summary>
    private static string Keyword(TypeDeclarationSyntax declaration) =>
        declaration is RecordDeclarationSyntax record && !record.ClassOrStructKeyword.IsKind(SyntaxKind.None)
            ? record.Keyword.ValueText + " " + record.ClassOrStructKeyword.ValueText
            : declaration.Keyword.ValueText;

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
