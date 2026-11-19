/**
 * Localization utility for clinical insights
 * Maps insight keys to localized messages and formats them with context data
 */

import { insightMessage } from "./insight-messages.svelte.js";

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
  const message = insightMessage(key, insight.context) ?? { title: key, description: "" };

  return {
    key,
    title: message.title,
    description: message.description,
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
