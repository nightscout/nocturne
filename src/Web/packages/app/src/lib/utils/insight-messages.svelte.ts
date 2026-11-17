/**
 * The copy for each clinical insight key. It is a module of its own, apart from the insight
 * logic, because wuchale extracts every capitalised string in a function of a `.svelte.ts`
 * file: the key fragments that logic matches on must not become catalogue messages.
 */

enum InsightKey {
  TimeInRangeExcellent = "TimeInRangeExcellent",
  NoSevereHypoglycemia = "NoSevereHypoglycemia",
  VariabilityControlled = "VariabilityControlled",
  AllTargetsMet = "AllTargetsMet",
  ReduceSevereHypoglycemia = "ReduceSevereHypoglycemia",
  ReduceHypoglycemia = "ReduceHypoglycemia",
  IncreaseTIR = "IncreaseTIR",
  ReduceSevereHyperglycemia = "ReduceSevereHyperglycemia",
  ReduceVariability = "ReduceVariability",
  TimeVeryLow = "TimeVeryLow",
  TimeBelowRange = "TimeBelowRange",
  TimeInRange = "TimeInRange",
  TimeVeryHigh = "TimeVeryHigh",
  Variability = "Variability",
  AllTargetsAchieved = "AllTargetsAchieved",
}

export type InsightTemplate = { title: string; description: string };

/**
 * Observations only: each states a figure against its consensus reference and leaves the
 * conclusion to the reader (PRODUCT.md, "Data, not advice"). Bands are named rather than given
 * as mg/dL thresholds so the copy holds in either unit. Built per call so wuchale reads them in
 * the current locale; the figures are template holes so each sentence is one catalogue message.
 */
export function insightMessage(
  key: string,
  context: Record<string, number> | undefined
): InsightTemplate | undefined {
  const figure = (name: string) => context?.[name]?.toFixed(1) ?? "—";
  const actual = figure("actual");
  const target = figure("target");

  switch (key) {
    case InsightKey.TimeInRangeExcellent:
      return {
        title: "Time in range at or above target",
        description: `Time in range was ${actual}% (consensus target: at least ${target}%).`,
      };
    case InsightKey.NoSevereHypoglycemia:
      return {
        title: "No time very low",
        description: "No readings in this period were very low.",
      };
    case InsightKey.VariabilityControlled:
      return {
        title: "Variability within target",
        description: `Coefficient of variation was ${figure("cv")}%, within its consensus target.`,
      };
    case InsightKey.AllTargetsMet:
    case InsightKey.AllTargetsAchieved:
      return {
        title: "All consensus targets met",
        description: "Every figure in this period was within its consensus target.",
      };
    case InsightKey.ReduceSevereHypoglycemia:
      return {
        title: "Time very low above target",
        description: "Time very low was above its consensus target.",
      };
    case InsightKey.ReduceHypoglycemia:
      return {
        title: "Time below range above target",
        description: "Time below range was above its consensus target.",
      };
    case InsightKey.IncreaseTIR:
      return {
        title: "Time in range below target",
        description: "Time in range was below its consensus target.",
      };
    case InsightKey.ReduceSevereHyperglycemia:
      return {
        title: "Time very high above target",
        description: "Time very high was above its consensus target.",
      };
    case InsightKey.ReduceVariability:
      return {
        title: "Variability above target",
        description: "Coefficient of variation was above its consensus target.",
      };
    case InsightKey.TimeVeryLow:
      return {
        title: "Time very low",
        description: `Time very low was ${actual}% (consensus target: under ${target}%).`,
      };
    case InsightKey.TimeBelowRange:
      return {
        title: "Time below range",
        description: `Time below range was ${actual}% (consensus target: under ${target}%).`,
      };
    case InsightKey.TimeInRange:
      return {
        title: "Time in range",
        description: `Time in range was ${actual}% (consensus target: at least ${target}%).`,
      };
    case InsightKey.TimeVeryHigh:
      return {
        title: "Time very high",
        description: `Time very high was ${actual}% (consensus target: under ${target}%).`,
      };
    case InsightKey.Variability:
      return {
        title: "Variability",
        description: `Coefficient of variation was ${actual}% (consensus target: at most ${target}%).`,
      };
  }
  return undefined;
}
