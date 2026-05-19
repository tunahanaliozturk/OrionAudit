namespace Moongazing.OrionAudit;

/// <summary>
/// Marks a partial class as the OrionAudit registration module for the consuming project.
/// The source generator emits a <c>RegisterAuditedTypes(AuditConfigurationBuilder)</c> method
/// and a static <c>SerializerContext</c> property on the marked class — the trim-safe / AOT-safe
/// way to wire OrionAudit instead of the reflective assembly scan.
/// </summary>
/// <example>
/// <code>
/// [OrionAuditModule]
/// public partial class AppAuditModule { }
///
/// services.AddOrionAudit&lt;AppDb&gt;(o =&gt;
/// {
///     AppAuditModule.RegisterAuditedTypes(o.ConfigurationBuilder);
///     o.UseJsonContext(AppAuditModule.SerializerContext);
/// });
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class OrionAuditModuleAttribute : Attribute
{
}
