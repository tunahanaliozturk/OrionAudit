namespace Moongazing.OrionAudit;

/// <summary>
/// Ambient audit state flowed via <see cref="AsyncLocal{T}"/>: a correlation id
/// (<see cref="Push(string)"/>) and the DI scope capture should resolve its collaborators from
/// (<see cref="PushServices(IServiceProvider)"/>).
/// <para>
/// Pushed correlation ids are preferred over <c>Activity.Current?.Id</c> by the interceptor when
/// stamping <see cref="AuditLog.CorrelationId"/>. Useful for background jobs, console runners, and
/// other contexts where no W3C trace is in flight.
/// </para>
/// </summary>
public static class AuditScope
{
    private static readonly AsyncLocal<string?> currentId = new();
    private static readonly AsyncLocal<IServiceProvider?> currentServices = new();

    /// <summary>The correlation id active on the current async-flow, or <c>null</c>.</summary>
    public static string? Current => currentId.Value;

    /// <summary>
    /// The DI scope pushed onto the current async-flow by <see cref="PushServices"/>, or
    /// <c>null</c> when none is active.
    /// </summary>
    public static IServiceProvider? CurrentServices => currentServices.Value;

    /// <summary>
    /// Pushes a new ambient correlation id; disposing the returned scope restores the previous
    /// value. Nests safely.
    /// </summary>
    public static IDisposable Push(string correlationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(correlationId);
        var previous = currentId.Value;
        currentId.Value = correlationId;
        return new PopOnDispose(previous);
    }

    /// <summary>
    /// Pushes the DI scope that audit capture must resolve <see cref="IAuditUserResolver"/>,
    /// <see cref="IAuditTenantResolver"/> and its other collaborators from, for the current
    /// async-flow. Disposing the returned scope restores the previous value; nests safely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>UseOrionAudit(sp)</c> captures whatever provider EF Core hands the options lambda. With
    /// <c>services.AddDbContext&lt;T&gt;((sp, o) =&gt; ...)</c> that lambda runs once per scope, so
    /// the captured provider <em>is</em> the request scope and nothing needs to be pushed. With
    /// <c>AddDbContextPool</c> and <c>AddDbContextFactory</c> the options are built <strong>once,
    /// from the root provider</strong>, so the captured provider is the root for the life of the
    /// process: a scoped <see cref="IAuditUserResolver"/> resolved from it is the first request's
    /// instance on every later save, and every audit row would carry the first request's user and
    /// tenant. This push is how those wirings supply the real per-request scope, and it wins over
    /// the captured provider whenever it is active.
    /// </para>
    /// <para>
    /// In ASP.NET Core, push it around the rest of the pipeline:
    /// <code>
    /// app.Use(async (http, next) =>
    /// {
    ///     using (AuditScope.PushServices(http.RequestServices))
    ///     {
    ///         await next();
    ///     }
    /// });
    /// </code>
    /// In a background job or console runner, push the scope you created for the unit of work.
    /// </para>
    /// </remarks>
    /// <param name="services">The scope to resolve audit collaborators from. Must stay alive for
    /// as long as the returned scope is not disposed.</param>
    public static IDisposable PushServices(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var previous = currentServices.Value;
        currentServices.Value = services;
        return new PopServicesOnDispose(previous);
    }

    private sealed class PopOnDispose : IDisposable
    {
        private readonly string? previous;
        public PopOnDispose(string? previous) => this.previous = previous;
        public void Dispose() => currentId.Value = previous;
    }

    private sealed class PopServicesOnDispose : IDisposable
    {
        private readonly IServiceProvider? previous;
        public PopServicesOnDispose(IServiceProvider? previous) => this.previous = previous;
        public void Dispose() => currentServices.Value = previous;
    }
}
