using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Sample;
using Moongazing.OrionAudit.Testing;

const string Sep = "============================================================";

Console.WriteLine("OrionAudit v0.1.0 — feature showcase");
Console.WriteLine(Sep);

await using var connection = new SqliteConnection("DataSource=:memory:");
await connection.OpenAsync();

var tenantResolver = new InMemoryAuditTenantResolver("tenant-acme");
var userResolver = new InMemoryAuditUserResolver(new AuditUser("alice@acme.io", "Alice Admin"));

var services = new ServiceCollection();
services.AddOrionAudit<ShopDb>(o => o
    .Audit<Order>()
    .Audit<Customer>(b => b
        .Hash(c => c.Email)        // PII -> SHA-256 hex, still equality-checkable
        .Redact(c => c.ApiKey)));   // truly secret -> "<redacted>", no change visibility
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
        l.EntityType.StartsWith("OrionAudit.Sample.Customer", StringComparison.Ordinal)
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
