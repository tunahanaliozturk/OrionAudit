using System.Text.Json.Nodes;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Tests;

public class SnapshotBuilderTests
{
    [Auditable]
    public sealed class Sample
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        [NotAuditable] public string Internal { get; set; } = "";
        [HashedAudit] public string Email { get; set; } = "";
        [RedactedAudit] public string Token { get; set; } = "";
    }

    private static IAuditConfiguration BuildConfig()
    {
        var builder = new AuditConfigurationBuilder();
        builder.Audit<Sample>();
        return builder.Build();
    }

    [Fact]
    public void Build_IncludesCapturedProperties()
    {
        var config = BuildConfig();
        var values = new Dictionary<string, object?>
        {
            ["Id"] = 1, ["Name"] = "Alice", ["Internal"] = "x", ["Email"] = "a@b.c", ["Token"] = "secret",
        };

        var node = SnapshotBuilder.Build(typeof(Sample), values, config);

        Assert.Equal(1, node["Id"]!.GetValue<int>());
        Assert.Equal("Alice", node["Name"]!.GetValue<string>());
    }

    [Fact]
    public void Build_ExcludesNotAuditableFields()
    {
        var config = BuildConfig();
        var values = new Dictionary<string, object?> { ["Id"] = 1, ["Internal"] = "x" };

        var node = SnapshotBuilder.Build(typeof(Sample), values, config);

        Assert.False(node.AsObject().ContainsKey("Internal"));
    }

    [Fact]
    public void Build_HashesHashedAuditFields_Deterministic()
    {
        var config = BuildConfig();
        var v1 = new Dictionary<string, object?> { ["Email"] = "user@example.com" };
        var v2 = new Dictionary<string, object?> { ["Email"] = "user@example.com" };

        var n1 = SnapshotBuilder.Build(typeof(Sample), v1, config);
        var n2 = SnapshotBuilder.Build(typeof(Sample), v2, config);

        var h1 = n1["Email"]!.GetValue<string>();
        var h2 = n2["Email"]!.GetValue<string>();
        Assert.Equal(h1, h2);
        Assert.Equal(64, h1.Length);   // SHA-256 hex length
        Assert.NotEqual("user@example.com", h1);
    }

    [Fact]
    public void Build_RedactsRedactedAuditFields()
    {
        var config = BuildConfig();
        var values = new Dictionary<string, object?> { ["Token"] = "secret-value" };
        var node = SnapshotBuilder.Build(typeof(Sample), values, config);
        Assert.Equal("<redacted>", node["Token"]!.GetValue<string>());
    }

    [Fact]
    public void Build_HandlesNullValues()
    {
        var config = BuildConfig();
        var values = new Dictionary<string, object?> { ["Name"] = null };
        var node = SnapshotBuilder.Build(typeof(Sample), values, config);
        Assert.Null((string?)node["Name"]);
    }
}
