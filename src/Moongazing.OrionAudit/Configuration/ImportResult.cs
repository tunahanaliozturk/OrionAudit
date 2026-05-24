namespace Moongazing.OrionAudit.Configuration;

/// <summary>
/// Result of <c>AuditImportBuilder.SaveAsync</c>. <see cref="Written"/> rows landed in
/// <see cref="AuditLog"/>; <see cref="Skipped"/> rows were already present (matched the
/// idempotency tag); <see cref="DeadLettered"/> rows failed and were written with
/// <see cref="AuditLog.Error"/> populated.
/// </summary>
public sealed record ImportResult(int Written, int Skipped, int DeadLettered);
