using Nocturne.Connectors.Core.Models;
using Xunit;

namespace Nocturne.Tests.Shared.Connectors;

/// <summary>
///     The requests that name a <c>to</c> and no <c>from</c>, which every connector reads from its
///     beginning (<see cref="SyncRequest.From"/>): a cursor reset and a ranged manual sync.
/// </summary>
public static class NoLowerBound
{
    public const string Reset = "cursor reset";
    public const string RangedSync = "ranged manual sync";

    public static TheoryData<string> Shapes => new() { Reset, RangedSync };

    public static SyncRequest Request(string shape, DateTime to, List<SyncDataType>? dataTypes = null) =>
        shape switch
        {
            Reset => SyncRequest.Reset(null, to, dataTypes),
            RangedSync => new SyncRequest { To = to, DataTypes = dataTypes ?? [] },
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
        };
}
