using System.Text;
using Microsoft.CodeAnalysis;

namespace Moongazing.OrionAudit.Generators;

internal static class HintNames
{
    /// <summary>
    /// Returns a hint-name stem built from the type's full metadata name: namespace, every enclosing
    /// type, and the type itself with its generic arity (for example <c>Orders.Outer_nInner</c>).
    /// Roslyn requires every hint name of a generator to be unique and throws
    /// <see cref="System.ArgumentException"/> on a collision, failing the whole generator run and
    /// dropping every file it had already produced. A stem built by replacing '.' with '_' collided
    /// as soon as one name contained an underscore or a nesting level was left out: namespace
    /// <c>A.B</c> + type <c>C_D</c> and namespace <c>A.B.C</c> + type <c>D</c> both became
    /// <c>A_B_C_D</c>. Dots are kept; every other character that is not a letter or digit gets an
    /// escape of its own, so two different metadata names can never produce the same stem. Mirrors
    /// <c>Moongazing.OrionGuard.Generators.HintNames</c> so the family stays consistent.
    /// </summary>
    public static string ForType(INamedTypeSymbol type)
    {
        var name = type.MetadataName;
        for (INamedTypeSymbol? containing = type.ContainingType; containing is not null; containing = containing.ContainingType)
        {
            name = containing.MetadataName + "+" + name;
        }

        if (!type.ContainingNamespace.IsGlobalNamespace)
        {
            name = type.ContainingNamespace.ToDisplayString() + "." + name;
        }

        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            switch (c)
            {
                case '.':
                    sb.Append(c);
                    break;
                case '_':
                    sb.Append("__");   // so an underscore in a name never reads as an escape
                    break;
                case '+':
                    sb.Append("_n");   // nesting
                    break;
                case '`':
                    sb.Append("_g");   // generic arity
                    break;
                default:
                    sb.Append(char.IsLetterOrDigit(c) ? c.ToString() : "_x");
                    break;
            }
        }

        return sb.ToString();
    }
}
