import type { ChartLabels } from "@nocturne/charts";
import { PREDICTIONS_UNAVAILABLE } from "#lib/api/predictions-messages.js";
import { mealPeriodLabel } from "./meal-period-labels.svelte";

/**
 * The glucose chart's words in the viewer's language. Built per call so wuchale reads them in
 * the current locale. A template that opens on a value is extracted only when marked.
 */
export function glucoseChartLabels(): ChartLabels {
  return {
    glucose: "Glucose",
    bolus: "Bolus",
    carbs: "Carbs",
    iob: "IOB",
    cob: "COB",
    iobCob: "IOB/COB",
    basal: "Basal",
    autoBasal: "Auto basal",
    tempBasal: "Temp basal",
    scheduled: "Scheduled",
    scheduledBasal: "Scheduled basal",
    suspended: "Suspended",
    percent: "Percent",
    pumpMode: "Pump mode",
    mode: "Mode",
    automatic: "Automatic",
    override: "Override",
    overrides: "Overrides",
    profile: "Profile",
    activity: "Activity",
    sleep: "Sleep",
    lastPumpSync: "Last pump sync",
    notReportedByPump: "Not reported by pump",
    fingerstickBg: "Fingerstick BG",
    inRange: "In range",
    veryHigh: "Very high",
    high: "High",
    low: "Low",
    veryLow: "Very low",
    sensor: "Sensor",
    site: "Site",
    reservoir: "Reservoir",
    battery: "Battery",
    loadingPredictions: "Loading predictions...",
    predictionsUnavailable: PREDICTIONS_UNAVAILABLE,
    unitsPerHour: "U/h",
    insulinUnits: "U",
    // @wc-include
    insulinAmount: (units) => `${units}U`,
    // @wc-include
    carbAmount: (grams) => `${grams}g`,
    // @wc-include
    basalRate: (rate) => `${rate}U/h`,
    basalInjection: (units, insulinName) =>
      // @wc-include
      insulinName ? `${units}U ${insulinName}` : `${units}U basal injection`,
    rangeLimits: (low, high) => `Range limits ${low} / ${high}`,
    target: (range) => `Target ${range}`,
    alarms: (count) => `Alarms (${count})`,
    scheduledTrackers: (count) => `Scheduled (${count})`,
    mealPeriod: (period) => mealPeriodLabel(period) ?? period,
  };
}
