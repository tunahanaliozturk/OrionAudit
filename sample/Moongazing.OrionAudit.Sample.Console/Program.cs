using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Sample;
using Moongazing.OrionAudit.Testing;

const string Sep = "============================================================";

Console.WriteLine("OrionAudit v0.2.0 — feature showcase");
Console.WriteLine(Sep);

await using var connection = new SqliteConnection("DataSource=:memory:");
await connection.OpenAsync();

var tenantResolver = new InMemoryAuditTenantResolver("tenant-acme");
var userResolver = new InMemoryAuditUserResolver(new AuditUser("alice@acme.io", "Alice Admin"));

var services = new ServiceCollection();
services.AddOrionAudit<ShopDb>(o =>
{
    // v0.3 source-gen path: RegisterAuditedTypes is emitted by the generator from the
    // [OrionAuditModule] partial class — no reflective assembly scan, AOT-safe.
    SampleAuditModule.RegisterAuditedTypes(o.ConfigurationBuilder);
    // Trim/AOT-safe JSON: route snapshot (de)serialisation through the source-gen context.
    o.UseJsonContext(SampleJsonContext.Default);
    // Fluent field rules still layer on top of the generator-registered types.
    o.Audit<Customer>(b => b
        .Hash(c => c.Email)        // PII -> SHA-256 hex, still equality-checkable
        .Redact(c => c.ApiKey));   // truly secret -> "<redacted>", no change visibility
});
services.AddSingleton(connection);
services.AddSingleton<IAuditTenantResolver>(tenantResolver);
services.AddSingleton<IAuditUserResolver>(userResolver);
services.AddDbContext<ShopDb>((sp, o) =>
    o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));

await using var sp = services.BuildServiceProvider();
await using (var scope = sp.CreateAsyncScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<ShopDb>();
    await ctx.Database.EnsureCreatedAsync();
}

await Section("1. INSERT — write a Customer and two Orders", async () =>
{
    await using var scope = sp.CreateAsyncScope();
    var ctx = scope.ServiceProvider.GetRequiredService<ShopDb>();
    var alice = new Customer { Name = "Alice", Email = "alice@example.com", ApiKey = "sk_live_top_secret" };
    ctx.Customers.Add(alice);
    ctx.Orders.Add(new Order { CustomerName = alice.Name, Status = "Pending", Total = 99.99m, InternalNote = "verify card" });
    ctx.Orders.Add(new Order { CustomerName = alice.Name, Status = "Pending", Total = 49.50m, InternalNote = "loyalty discount" });
    await ctx.SaveChangesAsync();
    PrintAuditTail(await Logs(ctx), n: 3);
});

await Section("2. UPDATE — promote one order to Shipped", async () =>
{
    await using var scope = sp.CreateAsyncScope();
    var ctx = scope.ServiceProvider.GetRequiredService<ShopDb>();
    var order = await ctx.Orders.FirstAsync(o => o.Status == "Pending");
    order.Status = "Shipped";
    order.InternalNote = "shipped via FedEx";
    await ctx.SaveChangesAsync();
    PrintAuditTail(await Logs(ctx), n: 1);
});

await Section("3. DELETE — remove a Customer (full snapshot is captured)", async () =>
{
    await using var scope = sp.CreateAsyncScope();
    var ctx = scope.ServiceProvider.GetRequiredService<ShopDb>();
    var customer = await ctx.Customers.FirstAsync();
    ctx.Customers.Remove(customer);
    await ctx.SaveChangesAsync();
    var deleteRow = (await Logs(ctx)).First(l => l.Action == AuditAction.Deleted);
    Console.WriteLine($"   Delete row Snapshot ({deleteRow.Snapshot!.Length} chars):");
    Console.WriteLine("     " + Truncate(deleteRow.Snapshot, 220));
});

await Section("4. SENSITIVE-FIELD HANDLING", async () =>
{
    await using var scope = sp.CreateAsyncScope();
    var ctx = scope.ServiceProvider.GetRequiredService<ShopDb>();
    var customerInsert = (await Logs(ctx)).First(l =>
        l.EntityType.StartsWith("Moongazing.OrionAudit.Sample.Customer", StringComparison.Ordinal)
        && l.Action == AuditAction.Inserted);
    Console.WriteLine($"   Customer Insert Diff: {Truncate(customerInsert.Diff, 220)}");
    Console.WriteLine("   Note Email = 64-char SHA-256 hex, ApiKey = \"<redacted>\".");
});

await Section("5. MULTI-TENANT READ FILTER (auto-applied)", async () =>
{
    await using var scope = sp.CreateAsyncScope();
    var ctx = scope.ServiceProvider.GetRequiredService<ShopDb>();
    var tenantA = await ctx.AuditFor<Order>().CountAsync();
    var crossTenant = await ctx.AuditFor<Order>(crossTenant: true).CountAsync();
    Console.WriteLine($"   Current tenant ({tenantResolver.TenantId}) sees {tenantA} Order audit rows.");
    Console.WriteLine($"   crossTenant=true sees {crossTenant} rows (would diverge if other tenants existed).");
});

await Section("6. TIME-TRAVEL RECONSTRUCTION", async () =>
{
    await using var scope = sp.CreateAsyncScope();
    var ctx = scope.ServiceProvider.GetRequiredService<ShopDb>();
    var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();
    var shippedOrder = await ctx.Orders.FirstAsync(o => o.Status == "Shipped");
    var current = await reconstructor.ReconstructAsync<Order>(shippedOrder.Id.ToString(), DateTime.UtcNow);
    Console.WriteLine($"   Reconstructed {shippedOrder.Id}: Status={current!.Status} Total={current.Total} Note={current.InternalNote}");
});

await Section("7. OPENTELEMETRY ACTIVITY", async () =>
{
    var captured = new List<Activity>();
    using var listener = new ActivityListener
    {
        ShouldListenTo = s => s.Name == OrionAuditTelemetry.ActivitySourceName,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        ActivityStopped = a => captured.Add(a),
    };
    ActivitySource.AddActivityListener(listener);

    await using var scope = sp.CreateAsyncScope();
    var ctx = scope.ServiceProvider.GetRequiredService<ShopDb>();
    ctx.Orders.Add(new Order { CustomerName = "Tracer", Status = "Pending", Total = 1m });
    await ctx.SaveChangesAsync();

    foreach (var activity in captured)
    {
        Console.WriteLine($"   {activity.OperationName,-24} status={activity.Status} duration={activity.Duration.TotalMilliseconds:F2}ms tags={Tags(activity)}");
    }
});

Console.WriteLine();
Console.WriteLine(Sep);
Console.WriteLine("v0.2.0 features tour");
Console.WriteLine(Sep);

await Section("8. AUDIT SCOPE (correlation override for background work)", async () =>
{
    using (AuditScope.Push("nightly-cleanup-2026-05-19"))
    {
        await using var scope = sp.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ShopDb>();
        ctx.Orders.Add(new Order { CustomerName = "Tracer-2", Status = "Pending", Total = 2m });
        await ctx.SaveChangesAsync();
    }
    await using var verifyScope = sp.CreateAsyncScope();
    var verifyCtx = verifyScope.ServiceProvider.GetRequiredService<ShopDb>();
    var last = await verifyCtx.AuditLog(crossTenant: true)
        .OrderByDescending(a => a.OccurredOnUtc)
        .FirstAsync();
    Console.WriteLine($"   Last audit row CorrelationId = {last.CorrelationId}");
});

await Section("9. PERIODIC SNAPSHOTTING (separate DB so it's isolated)", async () =>
{
    await using var snapConn = new SqliteConnection("DataSource=:memory:");
    await snapConn.OpenAsync();
    var snapServices = new ServiceCollection();
    snapServices.AddLogging();
    snapServices.AddOrionAudit<ShopDb>(o =>
    {
        SampleAuditModule.RegisterAuditedTypes(o.ConfigurationBuilder);
        o.UseJsonContext(SampleJsonContext.Default);
        o.SnapshotEvery(3);
    });
    snapServices.AddSingleton(snapConn);
    snapServices.AddDbContext<ShopDb>((p, o) =>
        o.UseSqlite(p.GetRequiredService<SqliteConnection>()).UseOrionAudit(p));
    await using var snapSp = snapServices.BuildServiceProvider();
    await using (var bootstrap = snapSp.CreateAsyncScope())
    {
        await bootstrap.ServiceProvider.GetRequiredService<ShopDb>().Database.EnsureCreatedAsync();
    }

    Guid orderId;
    await using (var scope = snapSp.CreateAsyncScope())
    {
        var ctx = scope.ServiceProvider.GetRequiredService<ShopDb>();
        var order = new Order { CustomerName = "Snap", Status = "0", Total = 0m };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();
        orderId = order.Id;
        for (var i = 1; i <= 8; i++)
        {
            order.Status = $"v{i}";
            await ctx.SaveChangesAsync();
        }
    }

    await using (var scope = snapSp.CreateAsyncScope())
    {
        var ctx = scope.ServiceProvider.GetRequiredService<ShopDb>();
        var totalRows = await ctx.AuditLogs.CountAsync();
        var snapshotRows = await ctx.AuditLogs.CountAsync(a => a.Snapshot != null && a.Action == AuditAction.Updated);
        Console.WriteLine($"   8 updates with SnapshotEvery(3) → {totalRows} audit rows, {snapshotRows} of them carry snapshots.");

        var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();
        var current = await reconstructor.ReconstructAsync<Order>(orderId.ToString(), DateTime.UtcNow.AddMinutes(1));
        Console.WriteLine($"   Reconstructed final Status = {current!.Status} (no Insert-forward replay needed — picked up the snapshot).");
    }
});

await Section("10. SOFT-DELETE CAPTURE", async () =>
{
    await using var sdConn = new SqliteConnection("DataSource=:memory:");
    await sdConn.OpenAsync();
    var sdServices = new ServiceCollection();
    sdServices.AddLogging();
    sdServices.AddOrionAudit<SoftDeleteDb>(o =>
    {
        SampleAuditModule.RegisterAuditedTypes(o.ConfigurationBuilder);
        o.UseJsonContext(SampleJsonContext.Default);
    });
    sdServices.AddSingleton(sdConn);
    sdServices.AddDbContext<SoftDeleteDb>((p, o) =>
        o.UseSqlite(p.GetRequiredService<SqliteConnection>()).UseOrionAudit(p));
    await using var sdSp = sdServices.BuildServiceProvider();
    await using (var bootstrap = sdSp.CreateAsyncScope())
    {
        await bootstrap.ServiceProvider.GetRequiredService<SoftDeleteDb>().Database.EnsureCreatedAsync();
    }

    await using (var scope = sdSp.CreateAsyncScope())
    {
        var ctx = scope.ServiceProvider.GetRequiredService<SoftDeleteDb>();
        var article = new Article { Title = "Hello", IsDeleted = false };
        ctx.Articles.Add(article);
        await ctx.SaveChangesAsync();
        article.IsDeleted = true;
        await ctx.SaveChangesAsync();

        foreach (var row in await ctx.AuditLogs.OrderBy(a => a.OccurredOnUtc).ToListAsync())
        {
            Console.WriteLine($"   {row.OccurredOnUtc:HH:mm:ss.fff}  {row.Action,-12}  snapshot={(row.Snapshot is null ? "no" : "yes")}");
        }
    }
});

Console.WriteLine();
Console.WriteLine(Sep);
Console.WriteLine("Sample complete.");

// ---- helpers ----

static async Task Section(string title, Func<Task> body)
{
    Console.WriteLine();
    Console.WriteLine($">> {title}");
    await body();
}

static async Task<List<AuditLog>> Logs(ShopDb ctx)
    => await ctx.AuditLog(crossTenant: true).OrderBy(a => a.OccurredOnUtc).ToListAsync();

static void PrintAuditTail(List<AuditLog> logs, int n)
{
    foreach (var log in logs.TakeLast(n))
    {
        var shortType = log.EntityType.Split(',')[0].Split('.').Last();
        Console.WriteLine($"   {log.OccurredOnUtc:HH:mm:ss.fff}  {log.Action,-8}  {shortType,-10}  user={log.UserId}");
    }
}

static string Tags(Activity a) =>
    string.Join(", ", a.Tags.Select(t => $"{t.Key}={t.Value}"));

static string Truncate(string value, int max)
    => value.Length <= max ? value : value[..max] + "…";
