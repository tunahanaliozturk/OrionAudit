namespace Moongazing.OrionAudit.Testing;

/// <summary>
/// Test double for <see cref="IAuditUserResolver"/>. Returns the configured user regardless of the
/// supplied service provider.
/// </summary>
public sealed class InMemoryAuditUserResolver : IAuditUserResolver
{
    /// <summary>Initializes a new resolver returning <paramref name="user"/> (default null).</summary>
    public InMemoryAuditUserResolver(AuditUser? user = null) => User = user;

    /// <summary>The user instance returned on resolve. Mutable so tests can swap mid-run.</summary>
    public AuditUser? User { get; set; }

    /// <inheritdoc />
    public AuditUser? Resolve(IServiceProvider serviceProvider) => User;
}
