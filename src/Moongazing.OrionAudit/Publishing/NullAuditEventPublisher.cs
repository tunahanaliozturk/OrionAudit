namespace Moongazing.OrionAudit.Publishing;

/// <summary>
/// No-op <see cref="IAuditEventPublisher"/>. Registered by default when nothing is wired so
/// existing consumers see zero behaviour change. Cheap, allocation-free, never throws.
/// </summary>
public sealed class NullAuditEventPublisher : IAuditEventPublisher
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly NullAuditEventPublisher Instance = new();

    /// <inheritdoc />
    public ValueTask PublishAsync(IReadOnlyList<AuditLogEvent> events, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}
