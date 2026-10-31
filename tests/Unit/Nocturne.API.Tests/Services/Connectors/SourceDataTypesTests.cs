using System.Reflection;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Models.Connectors;

namespace Nocturne.API.Tests.Services.Connectors;

[Trait("Category", "Unit")]
public class SourceDataTypesTests
{
    [Fact]
    public void EverySourceDataTypeNamesASyncDataType()
    {
        var names = typeof(SourceDataTypes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (Field: f.Name, Value: (string)f.GetRawConstantValue()!))
            .ToList();

        names.Should().NotBeEmpty();
        names.Should().OnlyContain(n => n.Field == n.Value && Enum.IsDefined(typeof(SyncDataType), n.Value),
            "the data-source summary, stats and delete results key their counts by SyncDataType name");
    }
}
