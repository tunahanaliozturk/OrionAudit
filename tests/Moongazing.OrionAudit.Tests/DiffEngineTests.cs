using System.Text.Json.Nodes;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;

namespace Moongazing.OrionAudit.Tests;

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

    [Fact]
    public void Compute_NestedObjectChange_ProducesScopedReplace()
    {
        var before = new JsonObject { ["Address"] = new JsonObject { ["City"] = "Ankara" } };
        var after = new JsonObject { ["Address"] = new JsonObject { ["City"] = "Istanbul" } };

        var diff = DiffEngine.Compute(before, after);
        var result = DiffEngine.Apply(before, diff);

        Assert.Contains("/Address/City", diff);
        Assert.Equal("Istanbul", result["Address"]!["City"]!.GetValue<string>());
    }

    [Fact]
    public void ComputeThenApply_ArrayGrows_RoundTrips()
    {
        var before = new JsonObject { ["Tags"] = new JsonArray("a") };
        var after = new JsonObject { ["Tags"] = new JsonArray("a", "b", "c") };

        var result = DiffEngine.Apply(before, DiffEngine.Compute(before, after));

        Assert.Equal(3, result["Tags"]!.AsArray().Count);
        Assert.Equal("c", result["Tags"]![2]!.GetValue<string>());
    }

    [Fact]
    public void ComputeThenApply_ArrayShrinks_RoundTrips()
    {
        var before = new JsonObject { ["Tags"] = new JsonArray("a", "b", "c") };
        var after = new JsonObject { ["Tags"] = new JsonArray("a") };

        var result = DiffEngine.Apply(before, DiffEngine.Compute(before, after));

        Assert.Single(result["Tags"]!.AsArray());
        Assert.Equal("a", result["Tags"]![0]!.GetValue<string>());
    }

    [Fact]
    public void ComputeThenApply_ArrayElementChanges_RoundTrips()
    {
        var before = new JsonObject { ["Tags"] = new JsonArray("a", "b") };
        var after = new JsonObject { ["Tags"] = new JsonArray("a", "z") };

        var result = DiffEngine.Apply(before, DiffEngine.Compute(before, after));

        Assert.Equal("z", result["Tags"]![1]!.GetValue<string>());
    }

    [Fact]
    public void ComputeThenApply_ValueTypeChanges_RoundTrips()
    {
        var before = new JsonObject { ["Field"] = "text" };
        var after = new JsonObject { ["Field"] = 42 };

        var result = DiffEngine.Apply(before, DiffEngine.Compute(before, after));

        Assert.Equal(42, result["Field"]!.GetValue<int>());
    }

    [Fact]
    public void ComputeThenApply_NullValue_RoundTrips()
    {
        var before = new JsonObject { ["Field"] = "text" };
        var after = new JsonObject { ["Field"] = null };

        var result = DiffEngine.Apply(before, DiffEngine.Compute(before, after));

        Assert.True(result.ContainsKey("Field"));
        Assert.Null(result["Field"]);
    }

    [Fact]
    public void ComputeThenApply_PointerEscapedKeys_RoundTrip()
    {
        var before = new JsonObject();
        var after = new JsonObject { ["a/b"] = 1, ["m~n"] = 2 };

        var result = DiffEngine.Apply(before, DiffEngine.Compute(before, after));

        Assert.Equal(1, result["a/b"]!.GetValue<int>());
        Assert.Equal(2, result["m~n"]!.GetValue<int>());
    }

    [Fact]
    public void Apply_HistoricalPatchWithMoveCopyTest_Replays()
    {
        // A hand-authored patch in the JsonPatch.Net-era format: move/copy/test ops
        // never emitted by the new Compute, but Apply must still replay them.
        var target = new JsonObject { ["a"] = 1 };
        var patch =
            "[{\"op\":\"add\",\"path\":\"/b\",\"value\":2}," +
            "{\"op\":\"copy\",\"from\":\"/a\",\"path\":\"/c\"}," +
            "{\"op\":\"move\",\"from\":\"/b\",\"path\":\"/d\"}," +
            "{\"op\":\"test\",\"path\":\"/a\",\"value\":1}," +
            "{\"op\":\"remove\",\"path\":\"/a\"}]";

        var result = DiffEngine.Apply(target, patch);

        Assert.False(result.ContainsKey("a"));
        Assert.False(result.ContainsKey("b"));
        Assert.Equal(1, result["c"]!.GetValue<int>());
        Assert.Equal(2, result["d"]!.GetValue<int>());
    }

    [Fact]
    public void Apply_FailingTestOp_Throws()
    {
        var target = new JsonObject { ["a"] = 1 };
        var patch = "[{\"op\":\"test\",\"path\":\"/a\",\"value\":999}]";

        Assert.Throws<OrionAuditException>(() => DiffEngine.Apply(target, patch));
    }

    [Fact]
    public void Apply_MalformedPatch_Throws()
    {
        var target = new JsonObject { ["a"] = 1 };

        Assert.Throws<OrionAuditException>(() => DiffEngine.Apply(target, "not json"));
    }
}
