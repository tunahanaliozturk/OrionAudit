namespace Moongazing.OrionAudit;

/// <summary>Helpers for serialising composite primary keys into the canonical <see cref="AuditLog.EntityId"/> form.</summary>
public static class AuditKey
{
    /// <summary>Separator between key components. Literal <c>|</c> characters in source values are percent-escaped.</summary>
    public const char Separator = '|';

    /// <summary>Renders the supplied key components into a stable string.</summary>
    /// <remarks>
    /// Each component is converted with <c>ToString()</c> (invariant for primitives). Literal
    /// <c>|</c> characters in source values are percent-escaped to <c>%7C</c> so the join is
    /// unambiguous. Single-component keys are returned verbatim (no escape) for backward
    /// compatibility with v0.1.0 audit rows.
    /// </remarks>
    public static string From(params object?[] components)
    {
        ArgumentNullException.ThrowIfNull(components);
        if (components.Length == 0)
        {
            throw new ArgumentException("At least one component is required.", nameof(components));
        }
        if (components.Length == 1)
        {
            return components[0]?.ToString()
                ?? throw new ArgumentException("Component cannot be null.", nameof(components));
        }
        return string.Join(Separator, components.Select(c =>
            (c?.ToString() ?? throw new ArgumentException("Component cannot be null.", nameof(components)))
                .Replace("|", "%7C", StringComparison.Ordinal)));
    }
}
