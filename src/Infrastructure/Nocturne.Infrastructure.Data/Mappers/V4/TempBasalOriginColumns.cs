using Nocturne.Core.Models.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>Reads the stored <c>origin</c> and <c>origin_evidence</c> strings of a temp basal.</summary>
internal static class TempBasalOriginColumns
{
    public static TempBasalOrigin OriginOf(string? stored) =>
        Enum.TryParse<TempBasalOrigin>(stored, out var origin) ? origin : TempBasalOrigin.Inferred;

    public static TempBasalOriginEvidence? EvidenceOf(string? stored) =>
        Enum.TryParse<TempBasalOriginEvidence>(stored, out var evidence) ? evidence : null;
}
