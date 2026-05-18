namespace OrionAudit.Testing;

/// <summary>Fluent assertions over a <see cref="AuditCapture"/>.</summary>
public sealed class AuditAssertions
{
    private readonly AuditCapture capture;

    internal AuditAssertions(AuditCapture capture) => this.capture = capture;

    /// <summary>Asserts that at least one audit row of <typeparamref name="T"/> with the given action was captured.</summary>
    public AuditAssertions HaveLogged<T>(AuditAction action)
    {
        if (!capture.For<T>().Any(a => a.Action == action))
        {
            throw new OrionAuditAssertionException(
                $"Expected {typeof(T).Name} {action} log but found none. " +
                $"Captured for {typeof(T).Name}: {string.Join(", ", capture.For<T>().Select(a => a.Action))}");
        }
        return this;
    }

    /// <summary>Asserts that at least one audit row of <typeparamref name="T"/> with the given action matches the predicate.</summary>
    public AuditAssertions HaveLogged<T>(AuditAction action, Func<AuditLog, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (!capture.For<T>().Any(a => a.Action == action && predicate(a)))
        {
            throw new OrionAuditAssertionException(
                $"Expected {typeof(T).Name} {action} log matching predicate but none found.");
        }
        return this;
    }

    /// <summary>Asserts that no audit row of <typeparamref name="T"/> was captured.</summary>
    public AuditAssertions NotHaveLogged<T>()
    {
        if (capture.For<T>().Any())
        {
            throw new OrionAuditAssertionException(
                $"Expected no {typeof(T).Name} logs but found {capture.For<T>().Count()}.");
        }
        return this;
    }

    /// <summary>Begins a count-then-type fluent assertion (e.g. <c>HaveLoggedExactly(2).Of&lt;Order&gt;()</c>).</summary>
    public CountAssertion HaveLoggedExactly(int expected) => new(this, capture, expected);

    /// <summary>Continuation that pairs an expected count with an entity type.</summary>
    public sealed class CountAssertion
    {
        private readonly AuditAssertions parent;
        private readonly AuditCapture capture;
        private readonly int expected;

        internal CountAssertion(AuditAssertions parent, AuditCapture capture, int expected)
        {
            this.parent = parent;
            this.capture = capture;
            this.expected = expected;
        }

        /// <summary>Specifies the entity type whose captured row count must equal the previously supplied number.</summary>
        public AuditAssertions Of<T>()
        {
            var actual = capture.For<T>().Count();
            if (actual != expected)
            {
                throw new OrionAuditAssertionException(
                    $"Expected exactly {expected} {typeof(T).Name} log(s), but found {actual}.");
            }
            return parent;
        }
    }
}
