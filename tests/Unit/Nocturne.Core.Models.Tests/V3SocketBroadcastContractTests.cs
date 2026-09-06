using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Nocturne.Core.Models.Tests;

/// <summary>
/// AAPS's NS v3 socket handler reads <c>doc.srvModified</c> with <c>JSONObject.getLong</c>
/// before it dispatches on the collection, so a realtime storage event whose doc carries no
/// numeric value there throws on the background thread and kills the AAPS process (#965).
/// <c>date</c> is parsed by Gson into a nullable field and is not load-bearing for the crash,
/// but it must agree with the V3 REST projection when present.
/// </summary>
[Trait("Category", "Unit")]
public class V3SocketBroadcastContractTests
{
    private const long Mills = 1_722_945_600_000;
    private const string CreatedAt = "2026-08-06T12:00:00.000Z";
    private static readonly long CreatedAtMills =
        DateTimeOffset.Parse(CreatedAt).ToUnixTimeMilliseconds();

    /// <summary>
    /// The upload shapes that reach a storage broadcast: an explicit <c>mills</c>, the AAPS
    /// <c>date</c>-without-<c>mills</c> shape, and the Loop/xDrip+ shape carrying only
    /// <c>created_at</c>.
    /// </summary>
    public static TheoryData<string, object, long> BroadcastDocs =>
        new()
        {
            { "entries (mills)", new Entry { Mills = Mills }, Mills },
            { "entries (dateString only)", new Entry { DateString = CreatedAt }, CreatedAtMills },
            { "entries (created_at only)", new Entry { CreatedAt = CreatedAt }, CreatedAtMills },
            { "treatments (mills)", new Treatment { Mills = Mills }, Mills },
            { "treatments (date only)", new Treatment { Date = Mills }, Mills },
            { "treatments (created_at only)", new Treatment { CreatedAt = CreatedAt }, CreatedAtMills },
            { "devicestatus (mills)", new DeviceStatus { Mills = Mills }, Mills },
            { "devicestatus (date only)", new DeviceStatus { Date = Mills }, Mills },
            { "devicestatus (created_at only)", new DeviceStatus { CreatedAt = CreatedAt }, CreatedAtMills },
            { "profile (mills)", new Profile { Mills = Mills }, Mills },
            { "profile (startDate only)", new Profile { Mills = 0, StartDate = CreatedAt }, CreatedAtMills },
            {
                "profile (created_at only)",
                new Profile
                {
                    Mills = 0,
                    StartDate = null!,
                    CreatedAt = CreatedAt,
                },
                CreatedAtMills
            },
            {
                "profile (unparseable startDate)",
                new Profile
                {
                    Mills = 0,
                    StartDate = "not-a-date",
                    CreatedAt = CreatedAt,
                },
                CreatedAtMills
            },
        };

    [Theory]
    [MemberData(nameof(BroadcastDocs))]
    public void BroadcastDoc_AlwaysCarriesANumericSrvModified(
        string shape,
        object doc,
        long expected
    )
    {
        var root = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(doc));

        root.TryGetProperty("srvModified", out var srvModified)
            .Should()
            .BeTrue($"AAPS calls getLong(\"srvModified\") on every {shape} storage event");
        srvModified
            .ValueKind.Should()
            .Be(JsonValueKind.Number, $"a null srvModified throws JSONException for {shape}");
        srvModified.GetInt64().Should().Be(expected);

        root.GetProperty("srvCreated").ValueKind.Should().Be(JsonValueKind.Number);
        root.GetProperty("srvCreated").GetInt64().Should().Be(expected);
    }

    /// <summary>
    /// The Loop payload from <c>DeviceStatusParityTests.PostDeviceStatus_WithLoop_ReturnsSameShape</c>:
    /// no <c>mills</c>, no <c>date</c>, only <c>created_at</c>. This is the doc that crashed AAPS.
    /// </summary>
    [Fact]
    public void LoopDeviceStatusUpload_IsBroadcastSafe()
    {
        var upload =
            "{\"device\":\"loop://iPhone\",\"created_at\":\""
            + CreatedAt
            + "\",\"loop\":{\"enacted\":{\"rate\":0.5,\"duration\":30}}}";

        var doc = JsonSerializer.Deserialize<DeviceStatus>(upload)!;
        var root = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(doc));

        doc.Mills.Should().Be(0, "the created_at leg must not rewrite the ingest timeline");
        root.GetProperty("srvModified").GetInt64().Should().Be(CreatedAtMills);
        root.GetProperty("srvCreated").GetInt64().Should().Be(CreatedAtMills);
    }

    [Fact]
    public void ExplicitSrvTimestamps_WinOverTheFallback()
    {
        var entry = new Entry
        {
            CreatedAt = CreatedAt,
            SrvModified = Mills,
            SrvCreated = Mills,
        };
        var deviceStatus = new DeviceStatus
        {
            CreatedAt = CreatedAt,
            SrvModified = Mills,
            SrvCreated = Mills,
        };

        entry.SrvModified.Should().Be(Mills);
        entry.SrvCreated.Should().Be(Mills);
        deviceStatus.SrvModified.Should().Be(Mills);
        deviceStatus.SrvCreated.Should().Be(Mills);
    }

    [Fact]
    public void UnparseableCreatedAt_YieldsNoSrvTimestamp()
    {
        // DeviceStatus.CreatedAt defaults to UtcNow, so this pins the parse leg only — it is
        // not a claim that a timestamp-less devicestatus resolves to null.
        new Entry { CreatedAt = "not-a-date" }.SrvModified.Should().BeNull();
        new DeviceStatus { CreatedAt = "not-a-date" }.SrvModified.Should().BeNull();
        new Profile { Mills = 0, StartDate = "not-a-date", CreatedAt = "also-not-a-date" }
            .SrvModified.Should()
            .BeNull();
    }

    /// <summary>
    /// An offset-bearing <c>created_at</c> must be honoured, and a zone-less one read as UTC
    /// rather than as server-local time — the box's timezone must not move the value.
    /// </summary>
    [Theory]
    [InlineData("2026-08-06T14:00:00+02:00", 1786017600000L)]
    [InlineData("2026-08-06T12:00:00Z", 1786017600000L)]
    [InlineData("2026-08-06T12:00:00", 1786017600000L)]
    [InlineData("2026-08-06T09:00:00-03:00", 1786017600000L)]
    public void CreatedAtOffsets_ResolveToTheSameInstant(string createdAt, long expected)
    {
        new Entry { CreatedAt = createdAt }.SrvModified.Should().Be(expected);
        new DeviceStatus { CreatedAt = createdAt }.SrvModified.Should().Be(expected);
        new Profile { Mills = 0, StartDate = createdAt }.SrvModified.Should().Be(expected);
    }

    /// <summary>
    /// A pre-1970 event time is still an event time. A <c>&gt; 0</c> guard drops it and puts
    /// the doc back on the crashing path.
    /// </summary>
    [Fact]
    public void NegativeMills_StillYieldsANumericSrvTimestamp()
    {
        new Entry { Mills = -86_400_000 }.SrvModified.Should().Be(-86_400_000);
        new DeviceStatus { Mills = -86_400_000 }.SrvModified.Should().Be(-86_400_000);
        new Profile { Mills = -86_400_000 }.SrvModified.Should().Be(-86_400_000);
        new Treatment { Mills = -86_400_000 }.SrvModified.Should().Be(-86_400_000);
        new Treatment { Mills = -86_400_000 }.SrvCreated.Should().Be(-86_400_000);
    }

    /// <summary>
    /// The shape the V1 ingest path produces for a pre-1970 <c>created_at</c>:
    /// DocumentProcessingService stamps Mills from it, and treatments is the collection AAPS
    /// writes most.
    /// </summary>
    [Fact]
    public void TreatmentWithAPre1970CreatedAt_IsBroadcastSafe()
    {
        var moonLanding = DateTimeOffset
            .Parse("1969-07-20T20:17:00.000Z")
            .ToUnixTimeMilliseconds();
        var treatment = new Treatment
        {
            EventType = "Note",
            CreatedAt = "1969-07-20T20:17:00.000Z",
            Mills = moonLanding,
        };

        var root = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(treatment));

        root.GetProperty("srvModified").ValueKind.Should().Be(JsonValueKind.Number);
        root.GetProperty("srvModified").GetInt64().Should().Be(moonLanding);
        root.GetProperty("date").GetInt64().Should().Be(moonLanding);
    }

    [Fact]
    public void NoTimestampAtAll_FabricatesNothing()
    {
        new Entry { Mills = 0 }.SrvModified.Should().BeNull();
        new Profile { Mills = 0, StartDate = "not-a-date" }.SrvModified.Should().BeNull();
    }

    /// <summary>
    /// Sub-second precision must survive: <c>srvModified</c> is AAPS's sync cursor, so
    /// truncating to whole seconds silently re-delivers or skips documents.
    /// </summary>
    [Fact]
    public void CreatedAtMilliseconds_AreNotTruncated()
    {
        new Entry { CreatedAt = "2026-08-06T12:00:00.123Z" }
            .SrvModified.Should()
            .Be(CreatedAtMills + 123);
    }

    /// <summary>
    /// An ambiguous numeric date is read month-first regardless of the server's culture.
    /// </summary>
    [Fact]
    public void AmbiguousDate_IsParsedCultureInvariantly()
    {
        var januarySecond = DateTimeOffset.Parse("2026-01-02T00:00:00Z").ToUnixTimeMilliseconds();

        new Entry { CreatedAt = "01/02/2026" }.SrvModified.Should().Be(januarySecond);
    }

    /// <summary>
    /// Declared precedence is Mills, then <c>date</c>, then <c>created_at</c> — pinned with all
    /// three disagreeing so no two legs can be swapped without a failure.
    /// </summary>
    [Fact]
    public void DeviceStatusSrvTimestamps_PreferMillsOverDateOverCreatedAt()
    {
        new DeviceStatus
        {
            Mills = Mills,
            Date = Mills + 1_000,
            CreatedAt = CreatedAt,
        }
            .SrvModified.Should()
            .Be(Mills);

        new DeviceStatus { Date = Mills + 1_000, CreatedAt = CreatedAt }
            .SrvModified.Should()
            .Be(Mills + 1_000);
    }

    /// <summary>
    /// Profiles are often uploaded without mills, and <c>startDate</c> is the timestamp the V3
    /// layer already treated as their event time.
    /// </summary>
    [Fact]
    public void ProfileSrvTimestamps_PreferStartDateOverCreatedAt()
    {
        new Profile
        {
            Mills = 0,
            StartDate = CreatedAt,
            CreatedAt = "2020-01-01T00:00:00.000Z",
        }
            .SrvModified.Should()
            .Be(CreatedAtMills);
    }

    [Theory]
    [MemberData(nameof(BroadcastDocs))]
    public void BroadcastDoc_NeverSerializesANonNumericDate(string shape, object doc, long _)
    {
        var root = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(doc));

        if (root.TryGetProperty("date", out var date))
        {
            date.ValueKind.Should()
                .Be(JsonValueKind.Number, $"{shape} must not broadcast date as null");
        }
    }

    [Fact]
    public void DocsWithAnEventTime_SerializeANumericDate()
    {
        foreach (object doc in new object[] { new Treatment { Mills = Mills }, new DeviceStatus { Mills = Mills } })
        {
            var root = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(doc));

            root.TryGetProperty("date", out var date).Should().BeTrue();
            date.ValueKind.Should().Be(JsonValueKind.Number);
            date.GetInt64().Should().Be(Mills);
        }
    }

    [Fact]
    public void Date_FallsBackToMillsNotToTheSrvTimestamps()
    {
        // Mills and the srv timestamps deliberately disagree here: date carries the event
        // time, not the server's modification time.
        var treatment = new Treatment
        {
            Mills = Mills,
            SrvCreated = Mills + 999_000,
            SrvModified = Mills + 999_000,
        };
        var deviceStatus = new DeviceStatus
        {
            Mills = Mills,
            SrvCreated = Mills + 999_000,
            SrvModified = Mills + 999_000,
        };

        treatment.Date.Should().Be(Mills);
        deviceStatus.Date.Should().Be(Mills);
    }

    [Fact]
    public void ExplicitDate_WinsOverTheMillsFallback()
    {
        new Treatment { Mills = Mills, Date = Mills + 5_000 }.Date.Should().Be(Mills + 5_000);
        new DeviceStatus { Mills = Mills, Date = Mills + 5_000 }.Date.Should().Be(Mills + 5_000);
    }

    [Fact]
    public void DeviceStatusDate_DoesNotReachCreatedAt()
    {
        // DeviceStatusDecomposer seeds Mills from Date when a doc arrives without one, then
        // falls through a richer precedence (OpenAPS IOB time, pump clock, ...) before
        // created_at. A created_at leg here would pre-empt that ordering.
        new DeviceStatus { CreatedAt = CreatedAt }.Date.Should().BeNull();
    }

    [Fact]
    public void AapsDateWithoutMillsUpload_KeepsTheUploadedDate()
    {
        var deviceStatus = new DeviceStatus { Date = Mills };

        deviceStatus.Mills.Should().Be(0);
        deviceStatus.Date.Should().Be(Mills);
    }

    [Fact]
    public void Treatment_WithOnlyDate_ResolvesMillsWithoutRecursing()
    {
        // Treatment.Mills resolves through date; date falls back to Mills. Reading either must
        // terminate — routing ResolveMills through the property stack-overflows the process.
        var treatment = new Treatment { Date = Mills };

        treatment.Mills.Should().Be(Mills);
        treatment.Date.Should().Be(Mills);
    }

    [Fact]
    public void Treatment_WithNoTimestampAtAll_TerminatesAndFabricatesNothing()
    {
        var treatment = new Treatment();

        treatment.Mills.Should().Be(0);
        treatment.Date.Should().BeNull();
        treatment.SrvModified.Should().BeNull();
    }

    /// <summary>
    /// An out-of-range <c>date</c> must be rejected at deserialization rather than saturated
    /// into <see cref="long.MaxValue"/>, which <c>Treatment.Created_at</c> then throws on.
    /// </summary>
    [Fact]
    public void OutOfRangeDate_IsRejectedNotSaturated()
    {
        var act = () => JsonSerializer.Deserialize<Treatment>("{\"date\":1.9e19}");

        act.Should().Throw<JsonException>();
    }
}
