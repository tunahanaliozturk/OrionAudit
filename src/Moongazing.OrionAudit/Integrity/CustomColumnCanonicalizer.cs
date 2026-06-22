using System.Globalization;

namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// Renders a custom-column value to the single, culture-invariant string form that is folded into the
/// chain MAC. Used on both sides of the chain - capture (reading the EF shadow value just written) and
/// verification (reading the same value back) - so the two must produce byte-identical output for an
/// untampered value. Reflection-free and Native-AOT clean.
/// </summary>
internal static class CustomColumnCanonicalizer
{
    /// <summary>
    /// Canonical invariant string for <paramref name="value"/>, or <see langword="null"/> when the
    /// value is null (so a null column and the string "null" never collide - <see cref="AuditEntryHasher"/>
    /// encodes null distinctly from any string).
    /// </summary>
    public static string? ToCanonicalString(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                return s;
            case bool b:
                // Lowercase, fixed tokens (not the culture/Boolean.ToString "True"/"False") so the
                // form never depends on framework casing conventions.
                return b ? "true" : "false";
            case DateTime dt:
                // Round-trip ("O") is invariant and unambiguous; the value is stored as captured.
                return dt.ToString("O", CultureInfo.InvariantCulture);
            case DateTimeOffset dto:
                return dto.ToString("O", CultureInfo.InvariantCulture);
            case Guid g:
                return g.ToString("D", CultureInfo.InvariantCulture);
            case byte[] bytes:
                return Convert.ToBase64String(bytes);
            case IFormattable formattable:
                // Numeric / enum / decimal etc. Invariant culture keeps the form stable across hosts.
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            default:
                return value.ToString();
        }
    }
}
