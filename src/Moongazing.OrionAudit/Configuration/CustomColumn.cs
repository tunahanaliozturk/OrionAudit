using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Moongazing.OrionAudit.Configuration;

/// <summary>
/// Information the value provider for a custom audit column receives at capture time.
/// Mirrors the data already in scope inside <c>AuditSaveChangesInterceptor</c>.
/// </summary>
public sealed record AuditColumnContext(
    object Entity,
    EntityEntry Entry,
    AuditAction Action,
    AuditUser? User,
    string? TenantId);

/// <summary>
/// A consumer-registered custom column on <see cref="AuditLog"/>. Created by
/// <c>OrionAuditOptions.AddColumn&lt;T&gt;</c> and consumed by
/// <c>AuditLogEntityTypeConfiguration</c> (shadow-property mapping), the interceptor
/// (value capture), and the dispatcher (async-mode value application).
/// </summary>
public sealed record CustomColumn(
    string Name,
    Type ClrType,
    Func<AuditColumnContext, object?> Provider)
{
    /// <summary>EF-mappable scalar types supported by <c>AddColumn&lt;T&gt;</c>.</summary>
    public static bool IsSupportedColumnType(Type t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var underlying = Nullable.GetUnderlyingType(t) ?? t;
        if (underlying.IsEnum)
        {
            return true;
        }
        return underlying == typeof(string)
            || underlying == typeof(Guid)
            || underlying == typeof(DateTime)
            || underlying == typeof(DateTimeOffset)
            || underlying == typeof(bool)
            || underlying == typeof(int)
            || underlying == typeof(long)
            || underlying == typeof(short)
            || underlying == typeof(byte)
            || underlying == typeof(decimal)
            || underlying == typeof(double)
            || underlying == typeof(float);
    }
}
