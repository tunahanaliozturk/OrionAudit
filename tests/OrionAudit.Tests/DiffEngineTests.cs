using System.Text.Json.Nodes;
using OrionAudit.Capture;

namespace OrionAudit.Tests;

public class DiffEngineTests
{
    [Fact]
    public void Compute_AddedProperties_ProducesAddOperations()
    {
        var before = new JsonObject();
        var after = new JsonObject { ["Status"] = "Pending", ["Total"] = 100 };

        var diff = DiffEngine.Compute(before, after);

        Assert.Contains("\"op\":\"add\"", diff);
        Assert.Contains("/Status", diff);
        Assert.Contains("/Total", diff);
    }

    [Fact]
    public void Compute_ChangedProperty_ProducesReplaceOperation()
    {
        var before = new JsonObject { ["Status"] = "Pending" };
        var after = new JsonObject { ["Status"] = "Shipped" };

        var diff = DiffEngine.Compute(before, after);

        Assert.Contains("\"op\":\"replace\"", diff);
        Assert.Contains("\"value\":\"Shipped\"", diff);
    }

    [Fact]
    public void Compute_RemovedProperty_ProducesRemoveOperation()
    {
        var before = new JsonObject { ["Status"] = "Pending", ["Note"] = "old" };
        var after = new JsonObject { ["Status"] = "Pending" };

        var diff = DiffEngine.Compute(before, after);

        Assert.Contains("\"op\":\"remove\"", diff);
        Assert.Contains("/Note", diff);
    }

    [Fact]
    public void Compute_NoChange_ProducesEmptyArray()
    {
        var before = new JsonObject { ["Status"] = "Pending" };
        var after = new JsonObject { ["Status"] = "Pending" };

        var diff = DiffEngine.Compute(before, after);

        Assert.Equal("[]", diff);
    }

    [Fact]
    public void Apply_AppliesDiffOntoTarget_AndReturnsResult()
    {
        var target = new JsonObject { ["Status"] = "Pending" };
        var diff = "[{\"op\":\"replace\",\"path\":\"/Status\",\"value\":\"Shipped\"}]";

        var result = DiffEngine.Apply(target, diff);

        Assert.Equal("Shipped", result["Status"]!.GetValue<string>());
    }
}
