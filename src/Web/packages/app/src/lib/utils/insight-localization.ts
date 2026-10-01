/**
 * Localization utility for clinical insights
 * Maps insight keys to localized messages and formats them with context data
 */

// Local type definition for insight keys
export enum InsightKey {
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

export interface LocalizedInsight {
  key?: string;
  context?: Record<string, number>;
}

export interface FormattedInsight {
  key: string;
  title: string;
  description: string;
  type: "success" | "warning" | "info" | "action";
  category: "pattern" | "treatment" | "lifestyle" | "trend";
  priority: number;
}

type InsightTemplate = { title: string; description: string };

// Observations only: each states a figure against its consensus reference and leaves the
// conclusion to the reader (PRODUCT.md, "Data, not advice"). Bands are named rather than given
// as mg/dL thresholds so the copy holds in either unit.
const insightMessages: Record<string, InsightTemplate> = {
  [InsightKey.TimeInRangeExcellent]: {
    title: "Time in range at or above target",
    description: "Time in range was {actual}% (consensus target: at least {target}%).",
  },
  [InsightKey.NoSevereHypoglycemia]: {
    title: "No time very low",
    description: "No readings in this period were very low.",
  },
  [InsightKey.VariabilityControlled]: {
    title: "Variability within target",
    description: "Coefficient of variation was {cv}%, within its consensus target.",
  },
  [InsightKey.AllTargetsMet]: {
    title: "All consensus targets met",
    description: "Every figure in this period was within its consensus target.",
  },
  [InsightKey.ReduceSevereHypoglycemia]: {
    title: "Time very low above target",
    description: "Time very low was above its consensus target.",
  },
  [InsightKey.ReduceHypoglycemia]: {
    title: "Time below range above target",
    description: "Time below range was above its consensus target.",
  },
  [InsightKey.IncreaseTIR]: {
    title: "Time in range below target",
    description: "Time in range was below its consensus target.",
  },
  [InsightKey.ReduceSevereHyperglycemia]: {
    title: "Time very high above target",
    description: "Time very high was above its consensus target.",
  },
  [InsightKey.ReduceVariability]: {
    title: "Variability above target",
    description: "Coefficient of variation was above its consensus target.",
  },
  [InsightKey.TimeVeryLow]: {
    title: "Time very low",
    description: "Time very low was {actual}% (consensus target: under {target}%).",
  },
  [InsightKey.TimeBelowRange]: {
    title: "Time below range",
    description: "Time below range was {actual}% (consensus target: under {target}%).",
  },
  [InsightKey.TimeInRange]: {
    title: "Time in range",
    description: "Time in range was {actual}% (consensus target: at least {target}%).",
  },
  [InsightKey.TimeVeryHigh]: {
    title: "Time very high",
    description: "Time very high was {actual}% (consensus target: under {target}%).",
  },
  [InsightKey.Variability]: {
    title: "Variability",
    description: "Coefficient of variation was {actual}% (consensus target: at most {target}%).",
  },
  [InsightKey.AllTargetsAchieved]: {
    title: "All consensus targets met",
    description: "Every figure in this period was within its consensus target.",
  },
};

/**
 * Format context values for display (round to 1 decimal place)
 */
function formatValue(value: number): string {
  return value.toFixed(1);
}

/**
 * Replace placeholders in message template with context values
 * Example: "Time is {actual}% (target: {target}%)" with { actual: 75, target: 70 }
 * becomes "Time is 75% (target: 70%)"
 */
function interpolateMessage(template: string, context: Record<string, number> | undefined): string {
  if (!context) return template;

  return template.replace(/{(\w+)}/g, (match, key) => {
    const value = context[key];
    return value !== undefined ? formatValue(value) : match;
  });
}

/**
 * Convert a backend LocalizedInsight to a formatted insight for display
 */
export function formatInsight(
  insight: LocalizedInsight,
  type: "success" | "warning" | "info" | "action",
  category: "pattern" | "treatment" | "lifestyle" | "trend",
  priority: number
): FormattedInsight {
  const key = insight.key ?? "";
  const templates = insightMessages[key] ?? { title: key, description: "" };

  return {
    key,
    title: templates.title,
    description: interpolateMessage(templates.description, insight.context),
    type,
    category,
    priority,
  };
}

/**
 * Get styling color for insight type
 */
export function getInsightColor(type: "success" | "warning" | "info" | "action"): string {
  switch (type) {
    case "success":
      return "text-success";
    case "warning":
      return "text-warning";
    case "info":
      return "text-info";
    case "action":
      return "text-primary";
  }
}

/**
 * Get insight type from context
 * Priority areas with warnings get "warning" type
 * Priority areas without warnings get "action" type
 * Strengths get "success" type
 * Actionable insights get "action" or "warning" depending on severity
 */
export function getInsightTypeFromKey(
  key: string,
  messageGroup: "strength" | "priority" | "actionable"
): "success" | "warning" | "info" | "action" {
  if (messageGroup === "strength") return "success";

  if (messageGroup === "priority") {
    if (key.includes("Severe") || key.includes("Reduce")) {
      return "warning";
    }
    return "action";
  }

  // Actionable insights
  if (key.includes("VeryLow") || key.includes("VeryHigh")) {
    return "warning";
  }
  if (key.includes("AllTargets")) {
    return "success";
  }
  return "action";
}
