namespace OrionAudit.Testing;

/// <summary>
/// Thrown by <see cref="AuditAssertions"/> when an expectation about captured audit rows fails.
/// Test runners (xUnit, NUnit, MSTest) treat any thrown exception as a test failure, so this
/// works without depending on a specific framework's assertion type.
/// </summary>
public sealed class OrionAuditAssertionException : Exception
{
    /// <summary>Initializes a new instance with the supplied message.</summary>
    public OrionAuditAssertionException(string message) : base(message) { }
}
