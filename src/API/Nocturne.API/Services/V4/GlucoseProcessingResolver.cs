using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.V4;

public class GlucoseProcessingResolver(IGlucoseProcessingConfigProvider configProvider) : IGlucoseProcessingResolver
{
    public async Task ResolveAsync(SensorGlucose model, string? glucoseProcessing, double? smoothedMgdl, double? unsmoothedMgdl, CancellationToken ct = default)
    {
        var payloadMgdl = model.Mgdl;

        // Case 1: Typed fields provided explicitly
        if (smoothedMgdl.HasValue || unsmoothedMgdl.HasValue)
        {
            model.SmoothedMgdl = smoothedMgdl;
            model.UnsmoothedMgdl = unsmoothedMgdl;

            if (glucoseProcessing is not null && Enum.TryParse<GlucoseProcessing>(glucoseProcessing, ignoreCase: true, out var gp))
                model.GlucoseProcessing = gp;

            if (smoothedMgdl.HasValue && unsmoothedMgdl.HasValue)
            {
                var preference = await configProvider.GetPreferredProcessingAsync(ct);
                model.Mgdl = preference switch
                {
                    GlucoseProcessing.Smoothed => smoothedMgdl.Value,
                    GlucoseProcessing.Unsmoothed => unsmoothedMgdl.Value,
                    _ => payloadMgdl > 0 ? payloadMgdl : smoothedMgdl.Value,
                };
            }
            else
            {
                model.Mgdl = payloadMgdl > 0 ? payloadMgdl : (smoothedMgdl ?? unsmoothedMgdl)!.Value;
            }

            return;
        }

        // Case 2: Resolve processing type from payload string or source defaults
        GlucoseProcessing? resolved = null;

        if (glucoseProcessing is not null && Enum.TryParse<GlucoseProcessing>(glucoseProcessing, ignoreCase: true, out var parsedGp))
        {
            resolved = parsedGp;
        }
        else
        {
            var defaults = await configProvider.GetSourceDefaultsAsync(ct);
            foreach (var rule in defaults)
            {
                var fieldValue = rule.Field.Equals("device", StringComparison.OrdinalIgnoreCase)
                    ? model.Device
                    : model.App;

                if (fieldValue is not null && fieldValue.StartsWith(rule.Match, StringComparison.OrdinalIgnoreCase))
                {
                    resolved = rule.Processing;
                    break;
                }
            }
        }

        if (resolved is null)
            return;

        model.GlucoseProcessing = resolved;

        switch (resolved)
        {
            case GlucoseProcessing.Smoothed:
                model.SmoothedMgdl = payloadMgdl;
                break;
            case GlucoseProcessing.Unsmoothed:
                model.UnsmoothedMgdl = payloadMgdl;
                break;
        }
    }
}
