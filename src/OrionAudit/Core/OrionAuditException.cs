namespace OrionAudit;

/// <summary>Base exception thrown by OrionAudit at runtime (e.g. reconstruction over a corrupted history).</summary>
public class OrionAuditException : Exception
{
    /// <summary>Initializes a new instance with the supplied message.</summary>
    public OrionAuditException(string message) : base(message) { }

    /// <summary>Initializes a new instance with the supplied message and inner exception.</summary>
    public OrionAuditException(string message, Exception inner) : base(message, inner) { }
}
