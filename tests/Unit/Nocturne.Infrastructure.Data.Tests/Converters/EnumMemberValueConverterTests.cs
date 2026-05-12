using System.Runtime.Serialization;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Converters;

namespace Nocturne.Infrastructure.Data.Tests.Converters;

public class EnumMemberValueConverterTests
{
    [Theory]
    [InlineData("critical", AlertRuleSeverity.Critical)]
    [InlineData("warning", AlertRuleSeverity.Warning)]
    [InlineData("info", AlertRuleSeverity.Info)]
    [InlineData("Critical", AlertRuleSeverity.Critical)]
    [InlineData("WARNING", AlertRuleSeverity.Warning)]
    public void FromProvider_ResolvesAlertRuleSeverity(string dbValue, AlertRuleSeverity expected)
    {
        var converter = new EnumMemberValueConverter<AlertRuleSeverity>();
        var fromProvider = converter.ConvertFromProviderExpression.Compile();

        var result = fromProvider(dbValue);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("threshold", AlertConditionType.Threshold)]
    [InlineData("rate_of_change", AlertConditionType.RateOfChange)]
    [InlineData("signal_loss", AlertConditionType.SignalLoss)]
    [InlineData("composite", AlertConditionType.Composite)]
    [InlineData("staleness", AlertConditionType.Staleness)]
    public void FromProvider_ResolvesAlertConditionType(string dbValue, AlertConditionType expected)
    {
        var converter = new EnumMemberValueConverter<AlertConditionType>();
        var fromProvider = converter.ConvertFromProviderExpression.Compile();

        var result = fromProvider(dbValue);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Warning", AlertRuleSeverity.Warning)]
    [InlineData("RateOfChange", AlertConditionType.RateOfChange)]
    [InlineData("SignalLoss", AlertConditionType.SignalLoss)]
    public void FromProvider_ResolvesByMemberName<TEnum>(string memberName, TEnum expected)
        where TEnum : struct, Enum
    {
        // Verifies that the converter resolves by C# member name (PascalCase)
        // in addition to the EnumMember attribute value.
        var converter = new EnumMemberValueConverter<TEnum>();
        var fromProvider = converter.ConvertFromProviderExpression.Compile();

        var result = fromProvider(memberName);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(AlertRuleSeverity.Critical, "critical")]
    [InlineData(AlertRuleSeverity.Warning, "warning")]
    [InlineData(AlertRuleSeverity.Info, "info")]
    public void ToProvider_UsesEnumMemberValue(AlertRuleSeverity value, string expected)
    {
        var converter = new EnumMemberValueConverter<AlertRuleSeverity>();
        var toProvider = converter.ConvertToProviderExpression.Compile();

        var result = toProvider(value);

        result.Should().Be(expected);
    }
}
