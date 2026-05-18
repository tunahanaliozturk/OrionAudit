namespace OrionAudit;

/// <summary>
/// Attribution information about the actor responsible for an audit event. Returned by
/// implementations of <see cref="IAuditUserResolver"/>.
/// </summary>
/// <param name="Id">Stable user identifier (e.g. <c>sub</c> claim, employee id, system principal).</param>
/// <param name="DisplayName">Optional human-readable name for UIs and reports.</param>
/// <param name="Type">Classification: <c>"user"</c> (default), <c>"system"</c>, <c>"job"</c>, etc.</param>
public sealed record AuditUser(string Id, string? DisplayName = null, string Type = "user");
