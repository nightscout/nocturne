/**
 * Localization utility for clinical insights
 * Maps insight keys to localized messages and formats them with context data
 */

import { InsightKey, type LocalizedInsight } from "$lib/api";

export interface FormattedInsight {
  key: string;
  title: string;
  description: string;
  type: "success" | "warning" | "info" | "action";
  category: "pattern" | "treatment" | "lifestyle" | "trend";
  priority: number;
}

type InsightTemplate = { title: string; description: string };

// Message templates keyed by InsightKey enum values
const insightMessages: Record<string, InsightTemplate> = {
  [InsightKey.TimeInRangeExcellent]: {
    title: "Excellent Time in Range",
    description:
      "You're spending {actual}% of your time in range — that's at or above the recommended {target}% target!",
  },
  [InsightKey.NoSevereHypoglycemia]: {
    title: "Minimal Low Blood Sugars",
    description: "Great job avoiding lows! You're spending very little time below 54 mg/dL.",
  },
  [InsightKey.VariabilityControlled]: {
    title: "Stable Glucose Levels",
    description: "Your glucose variability is {cv}% — that's nicely stable!",
  },
  [InsightKey.AllTargetsMet]: {
    title: "All Targets Achieved",
    description: "Excellent management across all metrics! Keep up the great work.",
  },
  [InsightKey.ReduceSevereHypoglycemia]: {
    title: "Reduce Severe Hypoglycemia",
    description:
      "Focus on reducing time very low (<54 mg/dL). Review overnight basal rates and CGM alerts.",
  },
  [InsightKey.ReduceHypoglycemia]: {
    title: "Reduce Overall Hypoglycemia",
    description: "Work on reducing time below 70 mg/dL. Consider adjusting correction factors.",
  },
  [InsightKey.IncreaseTIR]: {
    title: "Increase Time in Range",
    description: "Focus on improving time in your target range (70-180 mg/dL).",
  },
  [InsightKey.ReduceSevereHyperglycemia]: {
    title: "Reduce Severe Hyperglycemia",
    description: "Work on reducing time very high (>250 mg/dL). Review carb counting and doses.",
  },
  [InsightKey.ReduceVariability]: {
    title: "Reduce Glucose Variability",
    description: "Aim for more stable glucose patterns with fewer ups and downs.",
  },
  [InsightKey.TimeVeryLow]: {
    title: "Very Low Blood Sugar Alert",
    description:
      "Time very low (<54 mg/dL) is {actual}% (target: <{target}%). Review overnight basal rates and consider CGM alerts.",
  },
  [InsightKey.TimeBelowRange]: {
    title: "Time Below Range",
    description:
      "Time below range (<70 mg/dL) is {actual}% (target: <{target}%). Consider adjusting correction factors or meal timing.",
  },
  [InsightKey.TimeInRange]: {
    title: "Time in Range Opportunity",
    description:
      "Time in range is {actual}% (target: ≥{target}%). Focus on post-meal management and consistent timing.",
  },
  [InsightKey.TimeVeryHigh]: {
    title: "Very High Blood Sugar",
    description:
      "Time very high (>250 mg/dL) is {actual}% (target: <{target}%). Review carb counting and correction doses.",
  },
  [InsightKey.Variability]: {
    title: "Glucose Variability",
    description:
      "Glucose variability is {actual}% (target: <{target}%). Consider more consistent meal timing and composition.",
  },
  [InsightKey.AllTargetsAchieved]: {
    title: "All Targets Met",
    description: "Congratulations! You've achieved all clinical targets — keep up the excellent work!",
  },
};

/**
 * Format context values for display (round to 1 decimal place)
 */
function formatValue(value: number | undefined): string {
  if (value === undefined) return "–";
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
      return "text-green-600";
    case "warning":
      return "text-orange-600";
    case "info":
      return "text-blue-600";
    case "action":
      return "text-violet-600";
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
