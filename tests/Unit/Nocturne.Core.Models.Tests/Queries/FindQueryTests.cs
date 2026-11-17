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

    [Fact]
    public void MatchesResidual_IgnoresCapturedTimeBoundsButKeepsFieldConditions()
    {
        var query = FindQuery.Parse(
            "find[created_at][$gte]=2026-01-01&find[created_at][$lte]=2026-01-31&find[type]=Swim");

        query.MatchesResidual(Doc("{\"type\":\"Swim\",\"created_at\":\"2026-06-01T00:00:00Z\"}")).Should().BeTrue();
        query.MatchesResidual(Doc("{\"type\":\"Run\",\"created_at\":\"2026-01-10T00:00:00Z\"}")).Should().BeFalse();
        query.Matches(Doc("{\"type\":\"Swim\",\"created_at\":\"2026-06-01T00:00:00Z\"}")).Should().BeFalse();
    }

    [Fact]
    public void MatchesResidual_StillEvaluatesATimeConditionInsideAnOr()
    {
        var query = FindQuery.Parse("find[$or][0][created_at][$gte]=2026-01-01&find[$or][1][type]=Run");

        query.MatchesResidual(Doc("{\"type\":\"Swim\",\"created_at\":\"2025-06-01T00:00:00Z\"}")).Should().BeFalse();
        query.MatchesResidual(Doc("{\"type\":\"Swim\",\"created_at\":\"2026-06-01T00:00:00Z\"}")).Should().BeTrue();
        query.MatchesResidual(Doc("{\"type\":\"Run\",\"created_at\":\"2025-06-01T00:00:00Z\"}")).Should().BeTrue();
    }

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

    [Theory]
    [InlineData("find[]=")]
    [InlineData("find[]=x&token=y")]
    [InlineData("{\"$and\":[]}")]
    [InlineData("{\"$or\":[{}]}")]
    [InlineData("{\"date\":{}}")]
    public void IsEmpty_WhenNoConditionCanBeHonoured_IsTrue(string find)
    {
        FindQuery.Parse(find).IsEmpty.Should().BeTrue();
    }

    [Theory]
    [InlineData("find[type]=sgv")]
    [InlineData("find[]=x&find[type]=sgv")]
    [InlineData("{\"$and\":[{\"type\":\"sgv\"}]}")]
    [InlineData("{\"date\":{\"$gte\":1704067200000}}")]
    public void IsEmpty_WhenAConditionCanBeHonoured_IsFalse(string find)
    {
        FindQuery.Parse(find).IsEmpty.Should().BeFalse();
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

    [Fact]
    public void Regex_NestedQuantifier_MatchesInLinearTime()
    {
        var query = FindQuery.Parse("""{"notes":{"$regex":"^(a+)+$"}}""");
        var document = Doc($$"""{"notes":"{{new string('a', 20_000)}}!"}""");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var row = 0; row < 50; row++)
            query.Matches(document).Should().BeFalse();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        query.Matches(Doc("""{"notes":"aaaa"}""")).Should().BeTrue();
    }

    [Theory]
    [InlineData(@"(a)\1")]
    [InlineData("(?=a)a")]
    [InlineData("(?<!b)a")]
    [InlineData("a{100000}")]
    [InlineData("(unclosed")]
    public void Regex_ThatDoesNotCompile_MatchesNothingAndIsInexact(string pattern)
    {
        var query = FindQuery.Parse($"find[notes][$regex]={Uri.EscapeDataString(pattern)}");

        query.Matches(Doc("""{"notes":"aa"}""")).Should().BeFalse();
        query.HasInexactCondition.Should().BeTrue();
        BoundedRegex.Create(pattern).IsCompiled.Should().BeFalse();
    }

    [Fact]
    public void Regex_LongerThanTheCap_MatchesNothing()
    {
        const string letter = "[a-z]";
        var underCap = string.Concat(Enumerable.Repeat(letter, BoundedRegex.MaxLength / letter.Length));
        var pastCap = underCap + letter;

        BoundedRegex.Create(underCap).IsCompiled.Should().BeTrue();
        BoundedRegex.Create(pastCap).IsCompiled.Should().BeFalse();
        var value = new string('a', pastCap.Length / letter.Length);
        FindQuery.Parse($"find[notes][$regex]={Uri.EscapeDataString(pastCap)}").Matches(Doc($$"""{"notes":"{{value}}"}"""))
            .Should().BeFalse();
    }

    [Fact]
    public void Regex_OrdinaryPatterns_StillMatch()
    {
        FindQuery.Parse("find[dateString][$regex]=^2015-04.*T1[34]:0[0-5]")
            .Matches(Doc("""{"dateString":"2015-04-02T13:05:00.000Z"}""")).Should().BeTrue();
        FindQuery.Parse("""{"notes":{"$regex":"\\bsite\\b","$options":"i"}}""")
            .Matches(Doc("""{"notes":"Pump SITE changed"}""")).Should().BeTrue();
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

    #region Fields

    [Fact]
    public void Fields_NamesEveryFieldIncludingThoseInsideLogicalGroups()
    {
        var query = FindQuery.Parse("find[name]=apple&find[$or][0][category]=fruit&find[$or][1][carbs][$gt]=5");

        query.Fields.Should().BeEquivalentTo("name", "category", "carbs");
    }

    [Theory]
    [InlineData("find[name]=apple&find[a]]=b")]
    [InlineData("find[name]=apple&find[category][$options]=i")]
    public void Fields_LeavesOutAConditionTheParserCouldNotRead(string find)
    {
        var query = FindQuery.Parse(find);

        query.HasInexactCondition.Should().BeTrue();
        query.Fields.Should().NotContain("");
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

    #region Bare /pattern/flags on treatment fields

    [Theory]
    [InlineData("eventType", "Meal Bolus")]
    [InlineData("eventType", "Correction Bolus")]
    [InlineData("notes", "Bolus for lunch")]
    [InlineData("enteredBy", "BOLUS-app")]
    public void BareRegexLiteral_OnATreatmentField_MatchesAsARegexWithItsFlags(string field, string value)
    {
        var query = FindQuery.Parse($"find[{field}]=/bolus/i", FindQuery.TreatmentBareRegexFields);

        query.Matches(Doc($$"""{"{{field}}":"{{value}}"}""")).Should().BeTrue();
        query.Matches(Doc($$"""{"{{field}}":"Note"}""")).Should().BeFalse();
    }

    [Fact]
    public void BareRegexLiteral_WithoutTheCaseFlag_IsCaseSensitive()
    {
        var query = FindQuery.Parse("find[eventType]=/bolus/", FindQuery.TreatmentBareRegexFields);

        query.Matches(Doc("""{"eventType":"Meal Bolus"}""")).Should().BeFalse();
        query.Matches(Doc("""{"eventType":"meal bolus"}""")).Should().BeTrue();
    }

    [Fact]
    public void BareRegexLiteral_UrlEncoded_MatchesAsARegex()
    {
        var query = FindQuery.Parse("find%5BeventType%5D=%2FBG%20Check%2Fi", FindQuery.TreatmentBareRegexFields);

        query.Matches(Doc("""{"eventType":"bg check"}""")).Should().BeTrue();
    }

    [Fact]
    public void BareRegexLiteral_OnAnotherField_StaysLiteral()
    {
        var query = FindQuery.Parse("find[insulin]=/2/", FindQuery.TreatmentBareRegexFields);

        query.Matches(Doc("""{"insulin":"/2/"}""")).Should().BeTrue();
        query.Matches(Doc("""{"insulin":"2"}""")).Should().BeFalse();
    }

    [Fact]
    public void BareRegexLiteral_WithoutTheFieldSet_StaysLiteral()
    {
        var query = FindQuery.Parse("find[eventType]=/bolus/i");

        query.Matches(Doc("""{"eventType":"/bolus/i"}""")).Should().BeTrue();
        query.Matches(Doc("""{"eventType":"Meal Bolus"}""")).Should().BeFalse();
    }

    [Fact]
    public void ExplicitEq_OnATreatmentField_StaysLiteral()
    {
        var query = FindQuery.Parse("find[eventType][$eq]=/bolus/i", FindQuery.TreatmentBareRegexFields);

        query.Matches(Doc("""{"eventType":"/bolus/i"}""")).Should().BeTrue();
        query.Matches(Doc("""{"eventType":"Meal Bolus"}""")).Should().BeFalse();
    }

    [Fact]
    public void BareValueWithoutSlashes_OnATreatmentField_StaysLiteral()
    {
        var query = FindQuery.Parse("find[eventType]=Meal Bolus", FindQuery.TreatmentBareRegexFields);

        query.Matches(Doc("""{"eventType":"Meal Bolus"}""")).Should().BeTrue();
        query.Matches(Doc("""{"eventType":"Correction Bolus"}""")).Should().BeFalse();
    }

    [Fact]
    public void BareRegexLiteral_InsideAGroupClause_StaysLiteral()
    {
        var query = FindQuery.Parse("find[$or][0][eventType]=/bolus/i", FindQuery.TreatmentBareRegexFields);

        query.Matches(Doc("""{"eventType":"/bolus/i"}""")).Should().BeTrue();
        query.Matches(Doc("""{"eventType":"Meal Bolus"}""")).Should().BeFalse();
    }

    #endregion

    #region Conditions it cannot evaluate

    [Theory]
    [InlineData("""{"device":"rig","created_at":{}}""")]
    [InlineData("""{"device":"rig","created_at":{"$options":"i"}}""")]
    [InlineData("""{"device":"rig","$or":[]}""")]
    [InlineData("""{"device":"rig","$and":{"device":"rig"}}""")]
    [InlineData("""{"device":"rig","$or":[1]}""")]
    [InlineData("""{"device":"rig","$nor":[{"device":"x"}]}""")]
    [InlineData("""{"device":"rig","pump":{"$size":1}}""")]
    public void AnOperandOrOperatorItCannotRead_MatchesNothingAndIsInexact(string find)
    {
        var query = FindQuery.Parse(find);

        query.Matches(Doc("""{"device":"rig","created_at":"2024-01-15T00:00:00.000Z","pump":{}}""")).Should().BeFalse();
        query.HasFieldFiltersExcept("device").Should().BeTrue();
        query.HasInexactCondition.Should().BeTrue();
    }

    [Theory]
    [InlineData("find[created_at][$gte]=yesterday")]
    [InlineData("find[created_at][$gte]=")]
    [InlineData("find[created_at]=")]
    [InlineData("find[date][$in]=1|2")]
    [InlineData("find[created_at][$regex]=^2024")]
    [InlineData("""{"created_at":{"$gte":true}}""")]
    [InlineData("""{"created_at":{"$gte":1e300}}""")]
    [InlineData("find[pump][$where]=1")]
    public void ATimeConditionThatIsNotAnInstantOrAnUnknownOperator_IsInexact(string find)
    {
        FindQuery.Parse(find).HasInexactCondition.Should().BeTrue();
    }

    [Theory]
    [InlineData("find[created_at][$gte]=2024-01-15T00:00:00Z&find[device][$ne]=x&find[loop][$exists]=true")]
    [InlineData("find[_id][$in]=a&find[_id][$in]=b&find[created_at][$exists]=false")]
    [InlineData("""{"created_at":{"$gte":1705276800000,"$ne":1705320000000},"$or":[{}]}""")]
    public void ConditionsItEvaluatesAsNightscoutDoes_AreExact(string find)
    {
        FindQuery.Parse(find).HasInexactCondition.Should().BeFalse();
    }

    #endregion

    #region Options and exists

    [Theory]
    [InlineData("find[device][$options]=i&find[created_at][$lte]=2026-10-02T00:00:00Z")]
    [InlineData("find[device]=rig&find[device][$options]=i")]
    [InlineData("find[device][$regex]=rig&find[uploader][$options]=i")]
    [InlineData("find[$or][0][device][$options]=i&find[$or][1][device]=rig")]
    [InlineData("""{"device":{"$eq":"rig","$options":"i"}}""")]
    [InlineData("""{"device":{"$regex":"rig","$options":1},"pump":{"$options":"i"}}""")]
    public void AnOptionsWithoutARegex_IsNeverDroppedAndMatchesNothing(string find)
    {
        var query = FindQuery.Parse(find);

        query.HasInexactCondition.Should().BeTrue();
        query.Matches(Doc("""{"device":"rig","created_at":"2026-10-01T00:00:00.000Z","uploader":"rig"}""")).Should().BeFalse();
    }

    [Theory]
    [InlineData("find[device][$regex]=RIG&find[device][$options]=i")]
    [InlineData("""{"device":{"$regex":"RIG","$options":"i"}}""")]
    public void AnOptionsBesideItsRegex_IsExact(string find)
    {
        var query = FindQuery.Parse(find);

        query.HasInexactCondition.Should().BeFalse();
        query.Matches(Doc("""{"device":"rig"}""")).Should().BeTrue();
    }

    [Theory]
    [InlineData("find[loop][$exists]=yes")]
    [InlineData("find[loop][$exists]=")]
    [InlineData("find[loop][$exists]=2")]
    [InlineData("""{"loop":{"$exists":"yes"}}""")]
    [InlineData("""{"loop":{"$exists":{}}}""")]
    public void AnExistsThatIsNotABoolean_IsInexactAndMatchesNothing(string find)
    {
        var query = FindQuery.Parse(find);

        query.HasInexactCondition.Should().BeTrue();
        query.Matches(Doc("""{"loop":{}}""")).Should().BeFalse();
        query.Matches(Doc("""{"pump":{}}""")).Should().BeFalse();
    }

    [Theory]
    [InlineData("find[loop][$exists]=true", true)]
    [InlineData("find[loop][$exists]=TRUE", true)]
    [InlineData("find[loop][$exists]=1", true)]
    [InlineData("find[loop][$exists]=false", false)]
    [InlineData("find[loop][$exists]=0", false)]
    [InlineData("""{"loop":{"$exists":true}}""", true)]
    [InlineData("""{"loop":{"$exists":0}}""", false)]
    public void ABooleanExists_IsExact(string find, bool present)
    {
        var query = FindQuery.Parse(find);

        query.HasInexactCondition.Should().BeFalse();
        query.Matches(Doc("""{"loop":{}}""")).Should().Be(present);
        query.Matches(Doc("""{"pump":{}}""")).Should().Be(!present);
    }

    #endregion

    #region Field lookups

    [Theory]
    [InlineData("find[_id]=a", "a")]
    [InlineData("find[_id][$in]=a&find[_id][$in]=b", "a,b")]
    [InlineData("find[_id][$in]=a&find[_id][$in]=b&find[_id]=b", "b")]
    [InlineData("""{"_id":{"$in":["a","b"]}}""", "a,b")]
    [InlineData("find[_id]=a&find[_id]=b", "")]
    public void GetEqualityValues_IntersectsEveryEqualityAndInOnTheField(string find, string expected)
    {
        FindQuery.Parse(find).GetEqualityValues("_id")
            .Should().Equal(expected.Split(',', StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData("find[_id][$ne]=a")]
    [InlineData("find[_id]=a&find[_id][$regex]=a")]
    [InlineData("find[$or][0][_id]=a&find[$or][1][device]=x")]
    [InlineData("find[_ID]=a")]
    [InlineData("find[device]=x")]
    public void GetEqualityValues_IsNullWhenTheFieldIsNotConstrainedToAValueSet(string find)
    {
        FindQuery.Parse(find).GetEqualityValues("_id").Should().BeNull();
    }

    [Fact]
    public void AnOrdinalLookup_IgnoresACaseVariantOfTheField()
    {
        var query = FindQuery.Parse("find[Device]=rig");

        query.GetEqualityValue("device").Should().Be("rig");
        query.GetEqualityValue("device", StringComparison.Ordinal).Should().BeNull();
        query.HasFieldFiltersExcept("device").Should().BeFalse();
        query.HasFieldFiltersExcept("device", StringComparison.Ordinal).Should().BeTrue();
    }

    [Fact]
    public void MatchesAs_ReadsTheFieldAsTheGivenValue()
    {
        var query = FindQuery.Parse("find[_id]=abc-123&find[device]=rig");
        var document = new { _id = "5f8d0c1e8a7b4c3d9e5f0001", device = "rig" };

        query.Matches(document).Should().BeFalse();
        query.MatchesAs(document, "_id", "abc-123").Should().BeTrue();
        query.MatchesAs(document, "_id", "other").Should().BeFalse();
    }

    #endregion

    #region Sub-millisecond and decoded offsets

    [Theory]
    [InlineData("$gte", 1001L, null)]
    [InlineData("$gt", 1001L, null)]
    [InlineData("$lte", null, 1000L)]
    [InlineData("$lt", null, 1000L)]
    [InlineData("$eq", 1001L, 1000L)]
    public void ASubMillisecondBound_RoundsInwardToWholeMilliseconds(string op, long? from, long? to)
    {
        var query = FindQuery.Parse($"find[created_at][{op}]=1970-01-01T00:00:01.0005Z");

        query.FromMills.Should().Be(from);
        query.ToMills.Should().Be(to);
    }

    [Theory]
    [InlineData("$gte", false)]
    [InlineData("$gt", false)]
    [InlineData("$lte", true)]
    [InlineData("$lt", true)]
    [InlineData("$eq", false)]
    [InlineData("$ne", true)]
    public void ASubMillisecondOperand_ComparesAgainstTheDocumentsWholeMillisecond(string op, bool expected)
    {
        var query = FindQuery.Parse($"find[created_at][{op}]=1970-01-01T00:00:01.0005Z");

        query.Matches(Doc("""{"created_at":"1970-01-01T00:00:01.000Z"}""")).Should().Be(expected);
    }

    [Theory]
    [InlineData("find[created_at][$gte]=2024-01-15T10:00:00 10:00")]
    [InlineData("find[created_at][$gte]=2024-01-15T10:00:00+10:00")]
    [InlineData("find[created_at][$gte]=2024-01-15T10:00:00%2B10:00")]
    public void AnOffsetWhosePlusWasDecodedToASpace_IsRepaired(string find)
    {
        FindQuery.Parse(find).FromMills.Should().Be(1705276800000);
    }

    [Fact]
    public void AStrictBoundAtTheEndOfTheRange_DoesNotWrapAround()
    {
        var query = FindQuery.Parse($$$"""{"date":{"$gt":{{{long.MaxValue}}}},"mills":{"$lt":{{{long.MinValue}}}}}""");

        query.FromMills.Should().Be(long.MaxValue);
        query.ToMills.Should().Be(long.MinValue);
    }

    [Fact]
    public void BoundFields_LeaveABoundOnAnotherTimeFieldResidual()
    {
        var query = FindQuery.Parse(
            "find[timestamp][$gte]=2024-01-15T00:00:00Z&find[created_at][$lte]=2024-01-16T00:00:00Z",
            bareRegexFields: null, new HashSet<string> { "created_at" });

        query.FromMills.Should().BeNull();
        query.ToMills.Should().Be(1705363200000);
        query.HasFieldFilters.Should().BeTrue();
    }

    #endregion
}
