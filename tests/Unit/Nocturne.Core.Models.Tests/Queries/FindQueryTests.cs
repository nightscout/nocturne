using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models.Queries;
using Xunit;

namespace Nocturne.Core.Models.Tests.Queries;

/// <summary>
/// Tests for <see cref="FindQuery"/> covering both wire forms (querystring and JSON) and the
/// operator matrix real v1 clients send: LoopFollow's CAGE/SAGE/IAGE and type filters, Trio's
/// external-treatments import ($and groups of $ne, $exists) and remote delete ($eq on created_at).
/// </summary>
public class FindQueryTests
{
    private static JsonElement Doc(string json) => JsonDocument.Parse(json).RootElement;

    #region Parsing / shape

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    public void Parse_EmptyInputs_YieldEmptyQuery(string? find)
    {
        var query = FindQuery.Parse(find);

        query.IsEmpty.Should().BeTrue();
        query.HasFieldFilters.Should().BeFalse();
        query.FromMills.Should().BeNull();
        query.ToMills.Should().BeNull();
        query.Matches(Doc("""{"anything":1}""")).Should().BeTrue();
    }

    [Fact]
    public void Parse_MalformedJson_YieldsEmptyQuery()
    {
        var query = FindQuery.Parse("{not valid json");

        query.IsEmpty.Should().BeTrue();
        query.Matches(Doc("""{"a":1}""")).Should().BeTrue();
    }

    [Fact]
    public void Parse_QueryStringWithUnrelatedParams_IgnoresThem()
    {
        var query = FindQuery.Parse("count=10&find[eventType]=Note&token=abc");

        query.HasFieldFilters.Should().BeTrue();
        query.Matches(Doc("""{"eventType":"Note"}""")).Should().BeTrue();
        query.Matches(Doc("""{"eventType":"Bolus"}""")).Should().BeFalse();
    }

    [Fact]
    public void Parse_UrlEncodedKeys_AreDecoded()
    {
        var query = FindQuery.Parse("find%5Btype%5D%5B%24ne%5D=cal");

        query.Matches(Doc("""{"type":"sgv"}""")).Should().BeTrue();
        query.Matches(Doc("""{"type":"cal"}""")).Should().BeFalse();
    }

    #endregion

    #region Time bound extraction

    [Fact]
    public void TimeBounds_JsonNumericDateRange_ExtractedInclusive()
    {
        var query = FindQuery.Parse("""{"date":{"$gte":1000,"$lte":2000}}""");

        query.FromMills.Should().Be(1000);
        query.ToMills.Should().Be(2000);
        query.HasFieldFilters.Should().BeFalse();
    }

    [Fact]
    public void TimeBounds_QuerystringIsoCreatedAt_Extracted()
    {
        var query = FindQuery.Parse(
            "find[created_at][$gte]=2023-01-01T00:00:00.000Z&find[created_at][$lte]=2023-01-02T00:00:00.000Z");

        query.FromMills.Should().Be(1672531200000);
        query.ToMills.Should().Be(1672617600000);
        query.HasFieldFilters.Should().BeFalse();
    }

    [Fact]
    public void TimeBounds_DateStringIso_Extracted()
    {
        // LoopFollow entry polls: find[dateString][$gte]=<iso>
        var query = FindQuery.Parse("find[dateString][$gte]=2023-06-15T12:00:00Z");

        query.FromMills.Should().Be(1686830400000);
        query.HasFieldFilters.Should().BeFalse();
    }

    [Fact]
    public void TimeBounds_EqOnCreatedAt_YieldsExactWindow()
    {
        // Trio's remote treatment delete: DELETE ?find[created_at][$eq]=<iso>
        var query = FindQuery.Parse("find[created_at][$eq]=2023-01-01T10:30:00.000Z");

        query.FromMills.Should().Be(1672569000000);
        query.ToMills.Should().Be(1672569000000);
        query.HasFieldFilters.Should().BeFalse();
    }

    [Fact]
    public void TimeBounds_StrictOperators_TightenByOneMillisecond()
    {
        var query = FindQuery.Parse("""{"date":{"$gt":1000,"$lt":2000}}""");

        query.FromMills.Should().Be(1001);
        query.ToMills.Should().Be(1999);
    }

    [Fact]
    public void TimeBounds_NonTimeFields_AreNotTreatedAsTimeBounds()
    {
        // Previously {"sgv":{"$gte":180}} parsed from=180 and needed a plausibility hack downstream
        var query = FindQuery.Parse("""{"sgv":{"$gte":180}}""");

        query.FromMills.Should().BeNull();
        query.ToMills.Should().BeNull();
        query.HasFieldFilters.Should().BeTrue();
    }

    [Fact]
    public void TimeBounds_InsideOrGroups_AreNotPushedDown()
    {
        var query = FindQuery.Parse(
            """{"$or":[{"date":{"$gte":1000}},{"eventType":"Note"}]}""");

        query.FromMills.Should().BeNull();
        query.HasFieldFilters.Should().BeTrue();
    }

    [Fact]
    public void TimeBounds_MultipleLowerBounds_TakeTightest()
    {
        var query = FindQuery.Parse("""{"date":{"$gte":1000},"created_at":{"$gte":5000}}""");

        query.FromMills.Should().Be(5000);
    }

    [Fact]
    public void TimeBounds_CombinedWithFieldFilter_BothSurface()
    {
        var query = FindQuery.Parse("find[eventType]=Note&find[created_at][$gte]=2023-01-01T00:00:00Z");

        query.FromMills.Should().Be(1672531200000);
        query.HasFieldFilters.Should().BeTrue();
    }

    #endregion

    #region Equality and negation

    [Fact]
    public void Eq_ImplicitFromBareField_MatchesString()
    {
        // LoopFollow CAGE query: find[eventType]=Site Change&count=1
        var query = FindQuery.Parse("find[eventType]=Site Change");

        query.Matches(Doc("""{"eventType":"Site Change"}""")).Should().BeTrue();
        query.Matches(Doc("""{"eventType":"Sensor Change"}""")).Should().BeFalse();
    }

    [Fact]
    public void Eq_ExplicitOperator_MatchesString()
    {
        var query = FindQuery.Parse("find[eventType][$eq]=Sensor Change");

        query.Matches(Doc("""{"eventType":"Sensor Change"}""")).Should().BeTrue();
        query.Matches(Doc("""{"eventType":"Site Change"}""")).Should().BeFalse();
    }

    [Fact]
    public void Eq_NumericField_ComparesNumerically()
    {
        var query = FindQuery.Parse("find[carbs]=45");

        query.Matches(Doc("""{"carbs":45}""")).Should().BeTrue();
        query.Matches(Doc("""{"carbs":45.0}""")).Should().BeTrue();
        query.Matches(Doc("""{"carbs":46}""")).Should().BeFalse();
    }

    [Fact]
    public void Eq_IsoTimestamps_CompareByInstantNotFormatting()
    {
        var query = FindQuery.Parse("find[created_at][$eq]=2023-01-01T10:30:00.000Z");

        query.Matches(Doc("""{"created_at":"2023-01-01T10:30:00.000Z"}""")).Should().BeTrue();
        query.Matches(Doc("""{"created_at":"2023-01-01T10:30:00Z"}""")).Should().BeTrue();
        query.Matches(Doc("""{"created_at":"2023-01-01T10:30:00+00:00"}""")).Should().BeTrue();
        query.Matches(Doc("""{"created_at":"2023-01-01T10:30:01.000Z"}""")).Should().BeFalse();
    }

    [Fact]
    public void Ne_MatchesDifferentValueAndMissingField()
    {
        // Mongo semantics: $ne matches documents where the field is absent
        var query = FindQuery.Parse("find[enteredBy][$ne]=Trio");

        query.Matches(Doc("""{"enteredBy":"Loop"}""")).Should().BeTrue();
        query.Matches(Doc("""{"carbs":10}""")).Should().BeTrue();
        query.Matches(Doc("""{"enteredBy":null}""")).Should().BeTrue();
        query.Matches(Doc("""{"enteredBy":"Trio"}""")).Should().BeFalse();
    }

    [Fact]
    public void Ne_TypeNeCal_ExcludesCalibrations()
    {
        // LoopFollow entries poll: find[type][$ne]=cal
        var query = FindQuery.Parse("find[type][$ne]=cal");

        query.HasFieldFilters.Should().BeTrue();
        query.Matches(Doc("""{"type":"sgv"}""")).Should().BeTrue();
        query.Matches(Doc("""{"type":"cal"}""")).Should().BeFalse();
    }

    #endregion

    #region Range operators on fields

    [Theory]
    [InlineData("find[sgv][$gte]=180", 180, true)]
    [InlineData("find[sgv][$gte]=180", 179, false)]
    [InlineData("find[sgv][$gt]=180", 180, false)]
    [InlineData("find[sgv][$gt]=180", 181, true)]
    [InlineData("find[sgv][$lte]=70", 70, true)]
    [InlineData("find[sgv][$lte]=70", 71, false)]
    [InlineData("find[sgv][$lt]=70", 70, false)]
    [InlineData("find[sgv][$lt]=70", 69, true)]
    public void RangeOperators_OnNumericField(string find, int sgv, bool expected)
    {
        var query = FindQuery.Parse(find);

        query.Matches(Doc($$"""{"sgv":{{sgv}}}""")).Should().Be(expected);
    }

    [Fact]
    public void RangeOperators_MissingField_DoesNotMatch()
    {
        var query = FindQuery.Parse("find[carbs][$gte]=1");

        query.Matches(Doc("""{"insulin":2}""")).Should().BeFalse();
    }

    [Fact]
    public void RangeOperators_IsoDateStrings_CompareAsInstants()
    {
        var query = FindQuery.Parse("find[created_at][$gte]=2023-01-01T00:00:00Z");

        query.Matches(Doc("""{"created_at":"2023-06-01T00:00:00.000Z"}""")).Should().BeTrue();
        query.Matches(Doc("""{"created_at":"2022-06-01T00:00:00.000Z"}""")).Should().BeFalse();
    }

    #endregion

    #region $in / $nin

    [Fact]
    public void In_PipeSeparatedQuerystringForm()
    {
        var query = FindQuery.Parse("find[eventType][$in]=Site Change|Sensor Change");

        query.Matches(Doc("""{"eventType":"Site Change"}""")).Should().BeTrue();
        query.Matches(Doc("""{"eventType":"Sensor Change"}""")).Should().BeTrue();
        query.Matches(Doc("""{"eventType":"Note"}""")).Should().BeFalse();
    }

    [Fact]
    public void In_JsonArrayForm()
    {
        var query = FindQuery.Parse("""{"eventType":{"$in":["Site Change","Sensor Change"]}}""");

        query.Matches(Doc("""{"eventType":"Sensor Change"}""")).Should().BeTrue();
        query.Matches(Doc("""{"eventType":"Note"}""")).Should().BeFalse();
    }

    [Fact]
    public void In_BareJsonArray_ImpliesIn()
    {
        var query = FindQuery.Parse("""{"type":["sgv","mbg"]}""");

        query.Matches(Doc("""{"type":"mbg"}""")).Should().BeTrue();
        query.Matches(Doc("""{"type":"cal"}""")).Should().BeFalse();
    }

    [Fact]
    public void Nin_MatchesOutsideSetAndMissingField()
    {
        var query = FindQuery.Parse("find[eventType][$nin]=Note|BG Check");

        query.Matches(Doc("""{"eventType":"Bolus"}""")).Should().BeTrue();
        query.Matches(Doc("""{"carbs":5}""")).Should().BeTrue();
        query.Matches(Doc("""{"eventType":"Note"}""")).Should().BeFalse();
    }

    #endregion

    #region $exists

    [Fact]
    public void Exists_True_RequiresPresentNonNullField()
    {
        // Trio external-treatments import: find[carbs][$exists]=true
        var query = FindQuery.Parse("find[carbs][$exists]=true");

        query.Matches(Doc("""{"carbs":12}""")).Should().BeTrue();
        query.Matches(Doc("""{"carbs":0}""")).Should().BeTrue();
        query.Matches(Doc("""{"carbs":null}""")).Should().BeFalse();
        query.Matches(Doc("""{"insulin":1}""")).Should().BeFalse();
    }

    [Fact]
    public void Exists_False_MatchesAbsentOrNullField()
    {
        var query = FindQuery.Parse("find[duration][$exists]=false");

        query.Matches(Doc("""{"eventType":"Note"}""")).Should().BeTrue();
        query.Matches(Doc("""{"duration":null}""")).Should().BeTrue();
        query.Matches(Doc("""{"duration":30}""")).Should().BeFalse();
    }

    #endregion

    #region $regex

    [Fact]
    public void Regex_WithOptionsSibling_QuerystringForm()
    {
        var query = FindQuery.Parse("find[eventType][$regex]=temp&find[eventType][$options]=i");

        query.Matches(Doc("""{"eventType":"Temp Basal"}""")).Should().BeTrue();
        query.Matches(Doc("""{"eventType":"Bolus"}""")).Should().BeFalse();
    }

    [Fact]
    public void Regex_CaseSensitiveWithoutOptions()
    {
        var query = FindQuery.Parse("find[eventType][$regex]=temp");

        query.Matches(Doc("""{"eventType":"Temp Basal"}""")).Should().BeFalse();
        query.Matches(Doc("""{"eventType":"temp basal"}""")).Should().BeTrue();
    }

    [Fact]
    public void Regex_SlashLiteralForm_ParsesFlags()
    {
        var query = FindQuery.Parse("""{"notes":{"$regex":"/pump/i"}}""");

        query.Matches(Doc("""{"notes":"Pump site changed"}""")).Should().BeTrue();
        query.Matches(Doc("""{"notes":"sensor"}""")).Should().BeFalse();
    }

    [Fact]
    public void Regex_AnchoredPattern_Works()
    {
        var query = FindQuery.Parse("""{"eventType":{"$regex":"^Temp"}}""");

        query.Matches(Doc("""{"eventType":"Temp Basal"}""")).Should().BeTrue();
        query.Matches(Doc("""{"eventType":"High Temp"}""")).Should().BeFalse();
    }

    #endregion

    #region $and / $or groups

    [Fact]
    public void And_QuerystringGroups_TrioExternalTreatmentsImport()
    {
        // Trio excludes its own uploads with chained $ne clauses on every import cycle
        var query = FindQuery.Parse(
            "find[$and][0][enteredBy][$ne]=Trio&find[$and][1][enteredBy][$ne]=loop://iPhone" +
            "&find[$and][2][eventType][$ne]=Temp Basal");

        query.HasFieldFilters.Should().BeTrue();
        query.Matches(Doc("""{"enteredBy":"xdrip","eventType":"Meal Bolus"}""")).Should().BeTrue();
        query.Matches(Doc("""{"enteredBy":"Trio","eventType":"Meal Bolus"}""")).Should().BeFalse();
        query.Matches(Doc("""{"enteredBy":"loop://iPhone","eventType":"Meal Bolus"}""")).Should().BeFalse();
        query.Matches(Doc("""{"enteredBy":"xdrip","eventType":"Temp Basal"}""")).Should().BeFalse();
    }

    [Fact]
    public void And_JsonForm_AllClausesMustHold()
    {
        var query = FindQuery.Parse(
            """{"$and":[{"enteredBy":{"$ne":"Trio"}},{"carbs":{"$exists":true}}]}""");

        query.Matches(Doc("""{"enteredBy":"Loop","carbs":20}""")).Should().BeTrue();
        query.Matches(Doc("""{"enteredBy":"Trio","carbs":20}""")).Should().BeFalse();
        query.Matches(Doc("""{"enteredBy":"Loop"}""")).Should().BeFalse();
    }

    [Fact]
    public void Or_QuerystringGroups_AnyClauseMatches()
    {
        var query = FindQuery.Parse(
            "find[$or][0][eventType]=Note&find[$or][1][eventType]=BG Check");

        query.Matches(Doc("""{"eventType":"Note"}""")).Should().BeTrue();
        query.Matches(Doc("""{"eventType":"BG Check"}""")).Should().BeTrue();
        query.Matches(Doc("""{"eventType":"Bolus"}""")).Should().BeFalse();
    }

    [Fact]
    public void Or_JsonForm_AnyClauseMatches()
    {
        var query = FindQuery.Parse("""{"$or":[{"carbs":{"$gte":10}},{"insulin":{"$gte":1}}]}""");

        query.Matches(Doc("""{"carbs":15}""")).Should().BeTrue();
        query.Matches(Doc("""{"insulin":2}""")).Should().BeTrue();
        query.Matches(Doc("""{"carbs":5,"insulin":0.5}""")).Should().BeFalse();
    }

    [Fact]
    public void And_TimeBoundsInsideAndGroups_ArePushedDown()
    {
        var query = FindQuery.Parse(
            """{"$and":[{"created_at":{"$gte":"2023-01-01T00:00:00Z"}},{"eventType":{"$ne":"Note"}}]}""");

        query.FromMills.Should().Be(1672531200000);
        query.HasFieldFilters.Should().BeTrue();
    }

    #endregion

    #region GetEqualityValue / HasFieldFiltersExcept

    [Fact]
    public void GetEqualityValue_TopLevelEq_ReturnsValue()
    {
        FindQuery.Parse("find[type]=sgv").GetEqualityValue("type").Should().Be("sgv");
        FindQuery.Parse("""{"type":"mbg"}""").GetEqualityValue("type").Should().Be("mbg");
    }

    [Fact]
    public void GetEqualityValue_NonEqOrMissing_ReturnsNull()
    {
        FindQuery.Parse("find[type][$ne]=cal").GetEqualityValue("type").Should().BeNull();
        FindQuery.Parse("find[sgv][$gte]=180").GetEqualityValue("type").Should().BeNull();
    }

    [Fact]
    public void HasFieldFiltersExcept_IgnoresTheExceptedEquality()
    {
        var query = FindQuery.Parse("find[type]=sgv&find[date][$gte]=1672531200000");

        query.HasFieldFilters.Should().BeTrue();
        query.HasFieldFiltersExcept("type").Should().BeFalse();
    }

    [Fact]
    public void HasFieldFiltersExcept_StillTrueForOtherFilters()
    {
        var query = FindQuery.Parse("find[type]=sgv&find[device][$ne]=share2");

        query.HasFieldFiltersExcept("type").Should().BeTrue();
    }

    #endregion

    #region Matches over serialized models

    [Fact]
    public void Matches_Treatment_UsesWirePropertyNames()
    {
        var query = FindQuery.Parse("find[eventType]=Site Change");
        var match = new Treatment { EventType = "Site Change", Mills = 1000 };
        var noMatch = new Treatment { EventType = "Correction Bolus", Mills = 1000 };

        query.Matches(match).Should().BeTrue();
        query.Matches(noMatch).Should().BeFalse();
    }

    [Fact]
    public void Matches_Entry_TypeAndSgvFilters()
    {
        var typeQuery = FindQuery.Parse("find[type][$ne]=cal");
        var sgvQuery = FindQuery.Parse("find[sgv][$gte]=180");

        var sgvEntry = new Entry { Type = "sgv", Sgv = 200, Mills = 1000 };
        var calEntry = new Entry { Type = "cal", Mills = 1000 };

        typeQuery.Matches(sgvEntry).Should().BeTrue();
        typeQuery.Matches(calEntry).Should().BeFalse();
        sgvQuery.Matches(sgvEntry).Should().BeTrue();
        sgvQuery.Matches(new Entry { Type = "sgv", Sgv = 100, Mills = 1000 }).Should().BeFalse();
    }

    #endregion

    #region Fail-closed and edge semantics

    [Fact]
    public void NestedPathKeys_ParseAsDottedFieldPaths()
    {
        var query = FindQuery.Parse("find[boluscalc][cob]=5");

        query.HasFieldFilters.Should().BeTrue();
        query.Matches(Doc("""{"boluscalc":{"cob":5}}""")).Should().BeTrue();
        query.Matches(Doc("""{"boluscalc":{"cob":7}}""")).Should().BeFalse();
        query.Matches(Doc("""{"carbs":5}""")).Should().BeFalse();
    }

    [Fact]
    public void UnparseableFindKey_FailsClosed()
    {
        // A key the parser can't interpret must narrow the query to nothing — dropping it
        // would widen a field-filtered delete into a whole-window sweep
        var query = FindQuery.Parse(
            "find[$and][x][enteredBy][$ne]=Trio&find[created_at][$gte]=2023-01-01T00:00:00Z");

        query.HasFieldFilters.Should().BeTrue();
        query.Matches(Doc("""{"enteredBy":"xdrip","created_at":"2023-06-01T00:00:00Z"}""")).Should().BeFalse();
    }

    [Fact]
    public void ContradictoryTypeEqualities_StayResidualAndMatchNothing()
    {
        var query = FindQuery.Parse("find[type]=sgv&find[type]=mbg&find[date][$gte]=1672531200000");

        query.GetEqualityValue("type").Should().BeNull();
        query.HasFieldFiltersExcept("type").Should().BeTrue();
        query.Matches(Doc("""{"type":"sgv","date":1672531200001}""")).Should().BeFalse();
    }

    [Fact]
    public void NeNull_MatchesOnlyPresentNonNullFields()
    {
        var query = FindQuery.Parse("""{"duration":{"$ne":null}}""");

        query.Matches(Doc("""{"duration":30}""")).Should().BeTrue();
        query.Matches(Doc("""{"duration":null}""")).Should().BeFalse();
        query.Matches(Doc("""{"eventType":"Note"}""")).Should().BeFalse();
    }

    [Fact]
    public void EqNull_MatchesAbsentOrNullFields()
    {
        var query = FindQuery.Parse("""{"duration":null}""");

        query.Matches(Doc("""{"eventType":"Note"}""")).Should().BeTrue();
        query.Matches(Doc("""{"duration":null}""")).Should().BeTrue();
        query.Matches(Doc("""{"duration":30}""")).Should().BeFalse();
    }

    [Fact]
    public void TimeField_IsoOperandAgainstNumericField_ComparesByInstant()
    {
        // A captured time bound is re-evaluated inside filtered pages, so mixed representations
        // (ISO operand, epoch-ms document field) must agree with the pushdown
        var query = FindQuery.Parse(
            """{"date":{"$gte":"2023-01-01T00:00:00Z"},"sgv":{"$gte":180}}""");

        query.Matches(Doc("""{"date":1672531200001,"sgv":200}""")).Should().BeTrue();
        query.Matches(Doc("""{"date":1672531199999,"sgv":200}""")).Should().BeFalse();
    }

    [Fact]
    public void TimeField_NumericOperandAgainstIsoField_ComparesByInstant()
    {
        var query = FindQuery.Parse("find[created_at][$lte]=1672531200000");

        query.Matches(Doc("""{"created_at":"2022-12-31T00:00:00Z"}""")).Should().BeTrue();
        query.Matches(Doc("""{"created_at":"2023-01-02T00:00:00Z"}""")).Should().BeFalse();
    }

    [Fact]
    public void In_RepeatedArrayMarkerParams_FormOneValueSet()
    {
        var query = FindQuery.Parse("find[type][$in][]=sgv&find[type][$in][]=mbg");

        query.Matches(Doc("""{"type":"sgv"}""")).Should().BeTrue();
        query.Matches(Doc("""{"type":"mbg"}""")).Should().BeTrue();
        query.Matches(Doc("""{"type":"cal"}""")).Should().BeFalse();
    }

    [Fact]
    public void DuplicateOptionsParams_DoNotBreakTheRegex()
    {
        var query = FindQuery.Parse(
            "find[eventType][$regex]=temp&find[eventType][$options]=i&find[eventType][$options]=i");

        query.Matches(Doc("""{"eventType":"Temp Basal"}""")).Should().BeTrue();
        query.Matches(Doc("""{"eventType":"Bolus"}""")).Should().BeFalse();
    }

    [Fact]
    public void JsonFindContainingFindBracketLiteral_RoutesToJsonParser()
    {
        var query = FindQuery.Parse("""{"notes":{"$regex":"find[0-9]"}}""");

        query.Matches(Doc("""{"notes":"see find3 above"}""")).Should().BeTrue();
        query.Matches(Doc("""{"notes":"nothing"}""")).Should().BeFalse();
    }

    #endregion
}
