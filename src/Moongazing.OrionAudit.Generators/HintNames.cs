using System.Globalization;
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
    /// <c>A_B_C_D</c>.
    /// <para>
    /// The escape is injective, so two different metadata names cannot produce the same stem. Every
    /// <c>_</c> in the output starts an escape and each one is self-delimiting: <c>__</c> is a
    /// literal underscore, <c>_n</c> nesting, <c>_g</c> generic arity, and <c>_u</c> followed by
    /// exactly four hex digits is any other character, by value. Encoding the value matters — a C#
    /// identifier may legitimately contain a combining mark or connector punctuation, and one
    /// shared marker for all of them maps two valid, distinct types onto one stem.
    /// </para>
    /// <para>
    /// Derived from <c>Moongazing.OrionGuard.Generators.HintNames</c>. That copy still uses a shared
    /// marker for the fallback and has this collision; fixing it there is a separate change.
    /// </para>
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
                    if (char.IsLetterOrDigit(c))
                    {
                        sb.Append(c);
                    }
                    else
                    {
                        // The character's own value, not a shared marker. A C# identifier may
                        // legitimately contain a combining mark or connector punctuation, and one
                        // marker for all of them collides two valid modules back together.
                        sb.Append("_u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    }

                    break;
            }
        }

        return sb.ToString();
    }
}
