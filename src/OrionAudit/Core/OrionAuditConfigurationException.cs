namespace OrionAudit;

/// <summary>Thrown at startup when OrionAudit's configuration is invalid (e.g. missing PK, composite PK).</summary>
public sealed class OrionAuditConfigurationException : OrionAuditException
{
    /// <summary>Initializes a new instance with the supplied message.</summary>
    public OrionAuditConfigurationException(string message) : base(message) { }
}
