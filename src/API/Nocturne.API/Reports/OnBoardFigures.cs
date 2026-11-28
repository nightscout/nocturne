using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;

namespace Nocturne.API.Reports;

/// <summary>
/// Insulin and carbs on board now, from <see cref="IIobCobReader.ReadTotalAsync"/> on a live read, rounded by
/// <see cref="OnBoardRounding"/>.
/// </summary>
/// <param name="Insulin">Units; null when nothing stood behind it (<see cref="IobCobReading.HasIob"/>), which is no data and not zero.</param>
/// <param name="Carbs">Grams; null when nothing stood behind it (<see cref="IobCobReading.HasCob"/>).</param>
public sealed record OnBoardFigures(double? Insulin, double? Carbs)
{
    public static OnBoardFigures Of(IobCobReading reading) => new(
        reading.HasIob ? OnBoardRounding.Iob(reading.Iob.Iob) : null,
        reading.HasCob ? OnBoardRounding.Cob(reading.Cob!.Cob) : null);

    public static async Task<OnBoardFigures> ReadAsync(IIobCobReader reader, DateTimeOffset now, CancellationToken ct) =>
        Of(await reader.ReadTotalAsync(now.ToUnixTimeMilliseconds(), live: true, ct));
}
