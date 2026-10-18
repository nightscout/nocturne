using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Core.Utilities;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Utilities;

/// <summary>
/// <see cref="ConnectorFaults"/> decides which failures a connector survives and which fail the run.
/// A type that slides from one side to the other either hides a connector defect behind a skipped
/// record or turns one bad vendor record into a failed sync every cycle.
/// </summary>
public class ConnectorFaultsTests
{
    public static TheoryData<Exception> MalformedRecords => new()
    {
        new FormatException(),
        new ArgumentException(),
        new ArgumentOutOfRangeException(),
        new OverflowException(),
        new InvalidCastException(),
        new InvalidOperationException(),
        new KeyNotFoundException(),
        new JsonException(),
    };

    /// <summary>Connector defects, which a run has to surface rather than skip.</summary>
    public static TheoryData<Exception> Defects => new()
    {
        new NullReferenceException(),
        new NotSupportedException(),
        new NotImplementedException(),
        new IndexOutOfRangeException(),
    };

    [Theory]
    [MemberData(nameof(MalformedRecords))]
    public void IsMalformedRecord_RecordLevelParseFailure_IsRecoverable(Exception ex)
    {
        ConnectorFaults.IsMalformedRecord(ex).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Defects))]
    public void IsMalformedRecord_ConnectorDefect_IsNotRecoverable(Exception ex)
    {
        ConnectorFaults.IsMalformedRecord(ex).Should().BeFalse();
    }

    /// <summary>A fetch is not a record; a transport failure must not be skipped as one.</summary>
    [Fact]
    public void IsMalformedRecord_TransportFailure_IsNotARecordFault()
    {
        ConnectorFaults.IsMalformedRecord(new HttpRequestException()).Should().BeFalse();
    }

    public static TheoryData<Exception> SourceFailures => new()
    {
        new HttpRequestException(),
        new TimeoutException(),
        new IOException(),
        new InvalidDataException(),
        new JsonException(),
        new FormatException(),
    };

    [Theory]
    [MemberData(nameof(SourceFailures))]
    public void IsSourceFailure_SourceThatDidNotAnswerUsably_IsRecoverable(Exception ex)
    {
        ConnectorFaults.IsSourceFailure(ex, CancellationToken.None).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Defects))]
    public void IsSourceFailure_ConnectorDefect_IsNotRecoverable(Exception ex)
    {
        ConnectorFaults.IsSourceFailure(ex, CancellationToken.None).Should().BeFalse();
    }

    /// <summary>
    /// HttpClient reports its own timeout as a <see cref="TaskCanceledException"/> while the caller's
    /// token is still live. That is the source failing to answer, and costs only that fetch.
    /// </summary>
    [Fact]
    public void IsSourceFailure_HttpClientTimeout_IsASourceFailure()
    {
        using var caller = new CancellationTokenSource();

        ConnectorFaults.IsSourceFailure(new TaskCanceledException("timed out"), caller.Token)
            .Should().BeTrue();
    }

    /// <summary>A caller who cancelled has withdrawn the run; reporting a failed fetch would be a lie.</summary>
    [Fact]
    public void IsSourceFailure_CallerCancelled_IsNotASourceFailure()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();

        ConnectorFaults.IsSourceFailure(new OperationCanceledException(caller.Token), caller.Token)
            .Should().BeFalse();
        ConnectorFaults.IsSourceFailure(new TaskCanceledException(), caller.Token)
            .Should().BeFalse();
    }

    [Fact]
    public void NonNullRecords_DropsNullElementsAndKeepsTheRestInOrder()
    {
        string?[] records = ["a", null, "b", null, "c"];

        var kept = ConnectorFaults.NonNullRecords(records, NullLogger.Instance, "test", "letter");

        kept.Should().Equal("a", "b", "c");
    }

    [Fact]
    public void NonNullRecords_AbsentArray_IsEmpty()
    {
        ConnectorFaults.NonNullRecords<string>(null, NullLogger.Instance, "test", "letter")
            .Should().BeEmpty();
    }
}
