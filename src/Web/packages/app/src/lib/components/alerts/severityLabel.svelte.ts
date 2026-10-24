import type { AlertRuleSeverity } from "$api-clients";

export function severityLabel(
  s: AlertRuleSeverity | string | undefined
): string {
  switch (s) {
    case "critical":
      return "Critical";
    case "warning":
      return "Warning";
    case "info":
      return "Info";
    default:
      return s ?? "";
  }
}
