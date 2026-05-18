using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrionAudit;
using OrionAudit.Sample;

Console.WriteLine("OrionAudit v0.1.0 sample");
Console.WriteLine(new string('=', 60));

var connection = new SqliteConnection("DataSource=:memory:");
await connection.OpenAsync();

var services = new ServiceCollection();
services.AddOrionAudit<SampleDb>(o => o
    .Audit<Order>()
    .UserResolver<DemoUserResolver>());
services.AddSingleton(connection);
services.AddDbContext<SampleDb>((sp, o) =>
    o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));

await using var sp = services.BuildServiceProvider();
await using (var scope = sp.CreateAsyncScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<SampleDb>();
    await ctx.Database.EnsureCreatedAsync();
}

// 1) Insert a few orders
await using (var scope = sp.CreateAsyncScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<SampleDb>();
    ctx.Orders.Add(new Order { Status = "Pending", Total = 99.99m });
    ctx.Orders.Add(new Order { Status = "Pending", Total = 149.50m });
    await ctx.SaveChangesAsync();
    Console.WriteLine("  Inserted 2 orders");
}

// 2) Update one
await using (var scope = sp.CreateAsyncScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<SampleDb>();
    var first = await ctx.Orders.FirstAsync();
    first.Status = "Shipped";
    await ctx.SaveChangesAsync();
    Console.WriteLine($"  Updated order {first.Id} to Shipped");
}

// 3) Show audit log
await using (var scope = sp.CreateAsyncScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<SampleDb>();
    var logs = await ctx.AuditFor<Order>().OrderBy(a => a.OccurredOnUtc).ToListAsync();
    Console.WriteLine($"\n  AuditLog rows: {logs.Count}");
    foreach (var log in logs)
    {
        Console.WriteLine($"    {log.OccurredOnUtc:O}  {log.Action,-8}  EntityId={log.EntityId}  User={log.UserId}");
    }
}

// 4) Time-travel reconstruction
await using (var scope = sp.CreateAsyncScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<SampleDb>();
    var first = await ctx.Orders.FirstAsync();
    var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();
    var current = await reconstructor.ReconstructAsync<Order>(first.Id.ToString(), DateTime.UtcNow);
    Console.WriteLine($"\n  Reconstructed order {first.Id}:");
    Console.WriteLine($"    Status = {current!.Status}, Total = {current.Total}");
}

await connection.DisposeAsync();
Console.WriteLine("\n  Sample complete.");

