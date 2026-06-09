namespace Moongazing.OrionAudit.Tests;

using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;
using Moongazing.OrionAudit.Read;
using Xunit;

public sealed class ViewerLabelsTests
{
    public sealed class Order
    {
        public Guid Id { get; set; }
        public decimal SubTotal { get; set; }
        public string Status { get; set; } = "";
        public Address ShippingAddress { get; set; } = new();
    }

    public sealed class Address
    {
        public string Street { get; set; } = "";
    }

    private static AuditLog MakeRow(string entityType, string diff)
        => new()
        {
            EntityType = entityType,
            EntityId = "1",
            Action = AuditAction.Updated,
            OccurredOnUtc = DateTime.UtcNow,
            Diff = diff,
        };

    [Fact]
    public void Type_builder_Label_per_property_attaches_to_config()
    {
        var config = new AuditConfigurationBuilder()
            .Audit<Order>(b => b
                .Label(o => o.SubTotal, "Net")
                .Label(o => o.Status, "Order Status"))
            .Build();

        var tc = config.GetConfig(typeof(Order));
        Assert.NotNull(tc);
        Assert.Equal("Net", tc!.FieldLabel("SubTotal"));
        Assert.Equal("Order Status", tc.FieldLabel("Status"));
        Assert.Null(tc.FieldLabel("UnlabelledProperty"));
    }

    [Fact]
    public void Type_builder_Label_entity_attaches_EntityLabel()
    {
        var config = new AuditConfigurationBuilder()
            .Audit<Order>(b => b.Label("Sales Order"))
            .Build();

        var tc = config.GetConfig(typeof(Order));
        Assert.NotNull(tc);
        Assert.Equal("Sales Order", tc!.EntityLabel);
    }

    [Fact]
    public void Type_builder_Label_rejects_whitespace_label()
    {
        var builder = new AuditConfigurationBuilder();
        Assert.Throws<ArgumentException>(() => builder.Audit<Order>(b => b.Label(o => o.SubTotal, "  ")));
        Assert.Throws<ArgumentException>(() => builder.Audit<Order>(b => b.Label("  ")));
    }

    [Fact]
    public void Renderer_without_configuration_leaves_label_null()
    {
        var row = MakeRow(typeof(Order).AssemblyQualifiedName!,
            """[{"op":"replace","path":"/SubTotal","value":"42"}]""");

        var view = AuditViewRenderer.Render(row);

        Assert.Null(view.EntityDisplayLabel);
        Assert.Single(view.Changes);
        Assert.Null(view.Changes[0].DisplayLabel);
        Assert.Equal("/SubTotal", view.Changes[0].PropertyPath);
    }

    [Fact]
    public void Renderer_with_configuration_attaches_field_and_entity_labels()
    {
        var config = new AuditConfigurationBuilder()
            .Audit<Order>(b => b
                .Label("Sales Order")
                .Label(o => o.SubTotal, "Net"))
            .Build();

        var row = MakeRow(typeof(Order).AssemblyQualifiedName!,
            """[{"op":"replace","path":"/SubTotal","value":"42"}]""");

        var view = AuditViewRenderer.Render(row, config);

        Assert.Equal("Sales Order", view.EntityDisplayLabel);
        Assert.Single(view.Changes);
        Assert.Equal("Net", view.Changes[0].DisplayLabel);
    }

    [Fact]
    public void Renderer_nested_property_inherits_root_label()
    {
        // /ShippingAddress/Street labels under the root 'ShippingAddress' configuration so the
        // viewer can group nested changes under one label.
        var config = new AuditConfigurationBuilder()
            .Audit<Order>(b => b.Label(o => o.ShippingAddress, "Ship-to"))
            .Build();

        var row = MakeRow(typeof(Order).AssemblyQualifiedName!,
            """[{"op":"replace","path":"/ShippingAddress/Street","value":"5th"}]""");

        var view = AuditViewRenderer.Render(row, config);

        Assert.Equal("Ship-to", view.Changes[0].DisplayLabel);
    }

    [Fact]
    public void Renderer_unknown_entity_type_falls_back_to_null_label()
    {
        // EntityType from another assembly that the current process cannot resolve - Type.GetType
        // returns null and label resolution falls back to null instead of throwing.
        var config = new AuditConfigurationBuilder().Audit<Order>().Build();
        var row = MakeRow("Acme.Removed.Order, AcmeAssembly",
            """[{"op":"replace","path":"/SubTotal","value":"42"}]""");

        var view = AuditViewRenderer.Render(row, config);

        Assert.Null(view.EntityDisplayLabel);
        Assert.Null(view.Changes[0].DisplayLabel);
    }

    [Fact]
    public void Renderer_with_configuration_passes_custom_columns_through()
    {
        var config = new AuditConfigurationBuilder()
            .Audit<Order>(b => b.Label("Sales Order"))
            .Build();
        var customColumns = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["WorkflowStepId"] = 7,
        };
        var row = MakeRow(typeof(Order).AssemblyQualifiedName!, "[]");

        var view = AuditViewRenderer.Render(row, config, customColumns);

        Assert.Equal("Sales Order", view.EntityDisplayLabel);
        Assert.Equal(7, view.CustomColumns["WorkflowStepId"]);
    }
}
