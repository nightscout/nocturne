using Nocturne.Connectors.MyLife.Configurations.Constants;
using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;

namespace Nocturne.Connectors.MyLife.Mappers;

internal sealed class MyLifeTreatmentContext
{
    private MyLifeTreatmentContext(
        Dictionary<string, double> bolusCarbMatches,
        HashSet<long> suppressedCarbTimes,
        HashSet<long> tempBasalTimes,
        List<long> tempBasalProgramTimes,
        Dictionary<long, double> tempBasalProgramRates,
        int tempBasalConsolidationWindowMs,
        bool enableManualBgSync,
        bool enableMealCarbConsolidation,
        bool enableTempBasalConsolidation
    )
    {
        BolusCarbMatches = bolusCarbMatches;
        SuppressedCarbTimes = suppressedCarbTimes;
        TempBasalTimes = tempBasalTimes;
        TempBasalProgramTimes = tempBasalProgramTimes;
        TempBasalProgramRates = tempBasalProgramRates;
        TempBasalConsolidationWindowMs = tempBasalConsolidationWindowMs;
        EnableManualBgSync = enableManualBgSync;
        EnableMealCarbConsolidation = enableMealCarbConsolidation;
        EnableTempBasalConsolidation = enableTempBasalConsolidation;
    }

    internal Dictionary<string, double> BolusCarbMatches { get; }
    internal HashSet<long> SuppressedCarbTimes { get; }
    internal HashSet<long> TempBasalTimes { get; }
    internal List<long> TempBasalProgramTimes { get; }
    internal Dictionary<long, double> TempBasalProgramRates { get; }
    internal int TempBasalConsolidationWindowMs { get; }
    internal bool EnableManualBgSync { get; }
    internal bool EnableMealCarbConsolidation { get; }
    internal bool EnableTempBasalConsolidation { get; }

    internal static MyLifeTreatmentContext Create(
        IEnumerable<MyLifeEvent> events,
        bool enableManualBgSync,
        bool enableMealCarbConsolidation,
        bool enableTempBasalConsolidation,
        int tempBasalConsolidationWindowMinutes
    )
    {
        var suppressedCarbTimes = new HashSet<long>();
        var bolusCarbMatches = new Dictionary<string, double>();
        var tempBasalTimes = new HashSet<long>();
        var tempBasalProgramTimes = new List<long>();
        var tempBasalProgramRates = new Dictionary<long, double>();
        var tempBasalWindowMs = Math.Max(0, tempBasalConsolidationWindowMinutes) * 60 * 1000;
        if (enableTempBasalConsolidation)
        {
            foreach (var ev in events)
            {
                if (ev.Deleted) continue;

                if (ev.EventTypeId != MyLifeEventTypeIds.TempBasal) continue;

                var time = MyLifeMapperHelpers.ToUnixMilliseconds(ev.EventDateTime);
                tempBasalProgramTimes.Add(time);
            }

            var bestRateDistances = new Dictionary<long, long>();
            foreach (var ev in events)
            {
                if (ev.Deleted) continue;

                if (ev.EventTypeId != MyLifeEventTypeIds.BasalRate) continue;

                var info = MyLifeMapperHelpers.ParseInfo(ev.InformationFromDevice);
                if (!MyLifeMapperHelpers.TryGetInfoBool(info, MyLifeJsonKeys.IsTempBasalRate)) continue;

                if (
                    !MyLifeMapperHelpers.TryGetInfoDouble(
                        info,
                        MyLifeJsonKeys.BasalRate,
                        out var rate
                    )
                )
                    continue;

                var rateTime = MyLifeMapperHelpers.ToUnixMilliseconds(ev.EventDateTime);
                foreach (var programTime in tempBasalProgramTimes)
                {
                    var delta = Math.Abs(programTime - rateTime);
                    if (delta > tempBasalWindowMs) continue;

                    if (
                        bestRateDistances.TryGetValue(programTime, out var bestDelta)
                        && delta >= bestDelta
                    )
                        continue;
                    bestRateDistances[programTime] = delta;
                    tempBasalProgramRates[programTime] = rate;
                }
            }
        }

        if (!enableMealCarbConsolidation)
            return new MyLifeTreatmentContext(
                bolusCarbMatches,
                suppressedCarbTimes,
                tempBasalTimes,
                tempBasalProgramTimes,
                tempBasalProgramRates,
                tempBasalWindowMs,
                enableManualBgSync,
                enableMealCarbConsolidation,
                enableTempBasalConsolidation
            );

        var carbEvents = new List<CarbEvent>();
        foreach (var ev in events)
        {
            if (ev.Deleted) continue;

            if (ev.EventTypeId != MyLifeEventTypeIds.CarbCorrection) continue;

            if (!MyLifeMapperHelpers.TryParseDouble(ev.Value, out var carbValue)) continue;

            var time = MyLifeMapperHelpers.ToUnixMilliseconds(ev.EventDateTime);
            carbEvents.Add(new CarbEvent(time, carbValue));
        }

        foreach (var ev in events)
        {
            if (ev.Deleted) continue;

            if (
                ev.EventTypeId != MyLifeEventTypeIds.BolusNormal
                && ev.EventTypeId != MyLifeEventTypeIds.BolusSquare
                && ev.EventTypeId != MyLifeEventTypeIds.BolusDual
            )
                continue;

            var info = MyLifeMapperHelpers.ParseInfo(ev.InformationFromDevice);
            var embeddedCarbs = MyLifeMapperHelpers.ResolveBolusCarbs(info);
            var eventTime = MyLifeMapperHelpers.ToUnixMilliseconds(ev.EventDateTime);
            var window = MyLifeTimeConstants.CarbSuppressionWindowMs;
            var key = MyLifeMapperHelpers.BuildEventKey(ev);

            if (embeddedCarbs is > 0)
            {
                // Bolus has embedded carbs - use those and suppress any nearby carb events
                bolusCarbMatches[key] = embeddedCarbs.Value;
                foreach (
                    var carbEvent in carbEvents.Where(c => Math.Abs(c.Time - eventTime) <= window)
                )
                {
                    suppressedCarbTimes.Add(carbEvent.Time);
                    carbEvent.Matched = true;
                }
            }
            else
            {
                // Bolus has no embedded carbs - find the closest unmatched carb event within the window
                var closestCarb = carbEvents
                    .Where(c => !c.Matched && Math.Abs(c.Time - eventTime) <= window)
                    .OrderBy(c => Math.Abs(c.Time - eventTime))
                    .FirstOrDefault();

                if (closestCarb != null)
                {
                    bolusCarbMatches[key] = closestCarb.Carbs;
                    suppressedCarbTimes.Add(closestCarb.Time);
                    closestCarb.Matched = true;
                }
            }
        }

        return new MyLifeTreatmentContext(
            bolusCarbMatches,
            suppressedCarbTimes,
            tempBasalTimes,
            tempBasalProgramTimes,
            tempBasalProgramRates,
            tempBasalWindowMs,
            enableManualBgSync,
            enableMealCarbConsolidation,
            enableTempBasalConsolidation
        );
    }

    internal bool ShouldSuppressTempBasalRate(long mills)
    {
        if (!EnableTempBasalConsolidation) return false;

        var window = TempBasalConsolidationWindowMs;
        return TempBasalProgramTimes
            .Select(programTime => Math.Abs(programTime - mills))
            .Any(delta => delta <= window);
    }

    internal bool TryRegisterTempBasal(long mills)
    {
        return !EnableTempBasalConsolidation || TempBasalTimes.Add(mills);
    }

    internal bool TryGetTempBasalRate(long mills, out double rate)
    {
        rate = 0;
        return EnableTempBasalConsolidation && TempBasalProgramRates.TryGetValue(mills, out rate);
    }

    private sealed class CarbEvent(long time, double carbs)
    {
        public long Time { get; } = time;
        public double Carbs { get; } = carbs;
        public bool Matched { get; set; }
    }
}