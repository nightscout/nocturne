using System.Reflection;
using FluentAssertions;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Core.Models.Tests;

[Trait("Category", "Unit")]
public class RecordTypeKeysTests
{
    private static readonly Dictionary<string, string> Constants = typeof(RecordTypeKeys)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
        .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void Every_record_type_has_a_constant_and_no_constant_is_orphaned()
    {
        Constants.Keys.Should().BeEquivalentTo(Enum.GetNames<RecordType>());
    }

    [Theory]
    [MemberData(nameof(RecordTypes))]
    public void Constant_equals_the_computed_key(RecordType recordType)
    {
        Constants[recordType.ToString()].Should().Be(RecordTypeKeys.Key(recordType));
    }

    public static TheoryData<RecordType> RecordTypes()
    {
        var data = new TheoryData<RecordType>();
        foreach (var recordType in Enum.GetValues<RecordType>())
        {
            data.Add(recordType);
        }

        return data;
    }
}
