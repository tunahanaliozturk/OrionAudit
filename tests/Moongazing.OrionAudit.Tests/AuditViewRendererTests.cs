using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Read;

namespace Moongazing.OrionAudit.Tests;

public class AuditViewRendererTests
{
    private static AuditLog Log(string diff, AuditAction action = AuditAction.Updated) => new()
    {
        EntityType = "Some.Type, Some.Asm",
        EntityId = "1",
        Action = action,
        OccurredOnUtc = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc),
        UserDisplay = "Alice",
        Diff = diff,
    };

    [Fact]
    public void Render_Replace_ProducesModifiedFieldChange()
    {
        var view = AuditViewRenderer.Render(Log("""[{"op":"replace","path":"/Body","value":"v2"}]"""));
        var change = Assert.Single(view.Changes);
        Assert.Equal("/Body", change.PropertyPath);
        Assert.Equal(ChangeKind.Modified, change.ChangeKind);
        Assert.Equal("v2", change.NewValue);
    }

    [Fact]
    public void Render_Add_ProducesAddedFieldChange()
    {
        var view = AuditViewRenderer.Render(Log("""[{"op":"add","path":"/Tag","value":"x"}]"""));
        Assert.Equal(ChangeKind.Added, Assert.Single(view.Changes).ChangeKind);
    }

    [Fact]
    public void Render_Remove_ProducesRemovedFieldChange()
    {
        var view = AuditViewRenderer.Render(Log("""[{"op":"remove","path":"/Tag"}]"""));
        Assert.Equal(ChangeKind.Removed, Assert.Single(view.Changes).ChangeKind);
    }

    [Fact]
    public void Render_EmptyDiff_ProducesNoChanges()
        => Assert.Empty(AuditViewRenderer.Render(Log("[]")).Changes);

    [Fact]
    public void Render_CopiesEntryMetadata()
    {
        var view = AuditViewRenderer.Render(Log("[]", AuditAction.Inserted));
        Assert.Equal(AuditAction.Inserted, view.Action);
        Assert.Equal("Alice", view.UserDisplay);
        Assert.Equal(new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc), view.OccurredOnUtc);
    }

    [Fact]
    public void RenderMany_PreservesChronologicalOrder()
    {
        var older = Log("[]"); older.OccurredOnUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = Log("[]"); newer.OccurredOnUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var views = AuditViewRenderer.RenderMany(new[] { newer, older });
        Assert.True(views[0].OccurredOnUtc < views[1].OccurredOnUtc);
    }
}
