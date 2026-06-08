using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.Multitenancy;

[Trait("Category", "Unit")]
public class CategoryReadContextTests
{
    [Fact]
    public void New_IsNotShare_AndHasNoCsv()
    {
        var ctx = new CategoryReadContext();

        ctx.IsShare.Should().BeFalse();
        ctx.VisibleCategoriesCsv.Should().BeNull();
    }

    [Fact]
    public void MarkShare_SetsIsShare()
    {
        var ctx = new CategoryReadContext();

        ctx.MarkShare();

        ctx.IsShare.Should().BeTrue();
    }

    [Fact]
    public void SetVisibleCategories_OnShare_StoresCsv()
    {
        var ctx = new CategoryReadContext();
        ctx.MarkShare();

        ctx.SetVisibleCategories("glucose.read,treatments.read");

        ctx.VisibleCategoriesCsv.Should().Be("glucose.read,treatments.read");
    }

    [Fact]
    public void SetVisibleCategories_OnNonShare_IsIgnored()
    {
        // A request never marked as a share must not pick up a restrictive CSV — the
        // is_share=false policy clause already opens non-shares; this keeps the holder honest.
        var ctx = new CategoryReadContext();

        ctx.SetVisibleCategories("glucose.read");

        ctx.IsShare.Should().BeFalse();
        ctx.VisibleCategoriesCsv.Should().BeNull();
    }

    [Fact]
    public void SetVisibleCategories_EmptyOnShare_StoresEmpty_FailClosed()
    {
        // A share that unlocks no categories carries an empty CSV; the RLS policy then
        // denies every categorized table (fail-closed), so the value must be "" not null.
        var ctx = new CategoryReadContext();
        ctx.MarkShare();

        ctx.SetVisibleCategories(string.Empty);

        ctx.VisibleCategoriesCsv.Should().BeEmpty();
    }
}
