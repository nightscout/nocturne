using FluentAssertions;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Models.Tests.V4;

public class InsulinCatalogTests
{
    [Fact]
    public void GetAll_ShouldReturnNonEmptyList()
    {
        var catalog = InsulinCatalog.GetAll();
        catalog.Should().NotBeEmpty();
    }

    [Fact]
    public void GetAll_ShouldContainHumalog()
    {
        var catalog = InsulinCatalog.GetAll();
        catalog.Should().Contain(f => f.Id == "humalog" && f.Name == "Humalog (Insulin Lispro)");
    }

    [Fact]
    public void GetAll_ShouldHaveUniqueIds()
    {
        var catalog = InsulinCatalog.GetAll();
        catalog.Select(f => f.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void GetAll_AllEntriesShouldHaveValidDefaults()
    {
        var catalog = InsulinCatalog.GetAll();
        foreach (var formulation in catalog)
        {
            formulation.DefaultDia.Should().BeGreaterThan(0, $"{formulation.Name} DIA");
            formulation.DefaultPeak.Should().BeGreaterThan(0, $"{formulation.Name} Peak");
            formulation.Curve.Should().BeOneOf("rapid-acting", "ultra-rapid", "bilinear", $"{formulation.Name} Curve");
            formulation.Concentration.Should().BeGreaterThan(0, $"{formulation.Name} Concentration");
        }
    }

    [Fact]
    public void GetById_ShouldReturnMatchingFormulation()
    {
        var result = InsulinCatalog.GetById("fiasp");
        result.Should().NotBeNull();
        result!.Name.Should().Be("Fiasp (Faster Aspart)");
        result.Curve.Should().Be("ultra-rapid");
    }

    [Fact]
    public void GetById_UnknownId_ShouldReturnNull()
    {
        var result = InsulinCatalog.GetById("nonexistent");
        result.Should().BeNull();
    }

    [Fact]
    public void GetByCategory_ShouldFilterCorrectly()
    {
        var rapid = InsulinCatalog.GetByCategory(InsulinCategory.RapidActing);
        rapid.Should().NotBeEmpty();
        rapid.Should().OnlyContain(f => f.Category == InsulinCategory.RapidActing);
    }

    [Fact]
    public void Custom_ShouldExistWithDefaultValues()
    {
        var custom = InsulinCatalog.GetById("custom");
        custom.Should().NotBeNull();
        custom!.Category.Should().Be(InsulinCategory.RapidActing);
        custom.Concentration.Should().Be(100);
    }

    [Theory]
    [InlineData("humalog-u10", 10)]
    [InlineData("humalog-u40", 40)]
    [InlineData("humalog-u50", 50)]
    [InlineData("novorapid-u10", 10)]
    [InlineData("novorapid-u40", 40)]
    [InlineData("novorapid-u50", 50)]
    [InlineData("fiasp-u10", 10)]
    public void GetById_DilutedFormulations_ReturnsExpectedConcentration(string id, int expectedConcentration)
    {
        var formulation = InsulinCatalog.GetById(id);

        formulation.Should().NotBeNull();
        formulation!.Concentration.Should().Be(expectedConcentration);
    }

    [Fact]
    public void GetByCategory_RapidActing_IncludesDilutedVariants()
    {
        var rapidActing = InsulinCatalog.GetByCategory(InsulinCategory.RapidActing);

        rapidActing.Should().Contain(f => f.Concentration == 10);
        rapidActing.Should().Contain(f => f.Concentration == 40);
        rapidActing.Should().Contain(f => f.Concentration == 50);
    }
}
