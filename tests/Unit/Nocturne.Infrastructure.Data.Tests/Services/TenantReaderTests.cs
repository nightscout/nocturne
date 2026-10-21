using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.Services;

/// <summary>
/// <see cref="TenantReader"/> is carried only into a scope that still reads as no one, so carrying it
/// can narrow a scope but never move the tenant of one already in use.
/// </summary>
[Trait("Category", "Unit")]
public class TenantReaderTests
{
    private static readonly TenantContext Tenant = new(Guid.NewGuid(), "t", "T", true, false);

    private sealed class Accessor : ITenantAccessor
    {
        public TenantContext? Context { get; private set; }
        public Guid TenantId => Context?.TenantId ?? Guid.Empty;
        public bool IsResolved => Context is not null;
        public void SetTenant(TenantContext context) => Context = context;
    }

    private static ServiceProvider Scope() => new ServiceCollection()
        .AddSingleton<ITenantAccessor>(new Accessor())
        .AddSingleton<ICategoryReadContext>(new CategoryReadContext())
        .BuildServiceProvider();

    private static TenantReader ClampedShare()
    {
        var request = Scope();
        request.GetRequiredService<ITenantAccessor>().SetTenant(Tenant);
        var readContext = request.GetRequiredService<ICategoryReadContext>();
        readContext.MarkShare();
        readContext.SetVisibleCategories("glucose.read");
        return TenantReader.Of(request);
    }

    [Fact]
    public void CarryInto_AFreshScope_SetsTheWholeReader()
    {
        var scope = Scope();

        ClampedShare().CarryInto(scope);

        scope.GetRequiredService<ITenantAccessor>().TenantId.Should().Be(Tenant.TenantId);
        var readContext = scope.GetRequiredService<ICategoryReadContext>();
        readContext.IsShare.Should().BeTrue();
        readContext.VisibleCategoriesCsv.Should().Be("glucose.read");
        readContext.IsHistoryClamped.Should().BeTrue();
        TenantReader.ReadsAsNoOne(scope).Should().BeFalse();
    }

    [Fact]
    public void CarryInto_AScopeWithATenant_Throws()
    {
        var scope = Scope();
        scope.GetRequiredService<ITenantAccessor>().SetTenant(new TenantContext(Guid.NewGuid(), "o", "O", true, false));

        var act = () => ClampedShare().CarryInto(scope);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CarryInto_AClampedScope_Throws()
    {
        var scope = Scope();
        scope.GetRequiredService<ICategoryReadContext>().ClampMemberHistory();

        var act = () => ClampedShare().CarryInto(scope);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Narrowing_NeverWidens()
    {
        var reader = ClampedShare().ForTenant(new TenantContext(Guid.NewGuid(), "o", "O", true, false));

        reader.ClampedToDay(false).HistoryClamped.Should().BeTrue("ClampedToDay(false) leaves a clamp in place");
        reader.IsShare.Should().BeTrue();
        reader.VisibleCategoriesCsv.Should().Be("glucose.read");
    }
}
