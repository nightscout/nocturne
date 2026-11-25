import { AlertRuleSeverity } from "@nocturne/api-client";

/** What a severity does, for the person who chooses one. */
export function severityDescription(s: AlertRuleSeverity): string {
  switch (s) {
    case AlertRuleSeverity.Info:
      return "Good to know. No action is necessary now.";
    case AlertRuleSeverity.Warning:
      return "Needs attention soon.";
    case AlertRuleSeverity.Critical:
      return "Needs action now. Fills the screen in Nocturne and also alerts during Do Not Disturb.";
  }
}

export function severityLabel(
  s: AlertRuleSeverity | string | undefined
): string {
  switch (s) {
    case AlertRuleSeverity.Critical:
      return "Critical";
    case AlertRuleSeverity.Warning:
      return "Warning";
    case AlertRuleSeverity.Info:
      return "Info";
    default:
      return s ?? "";
  }
}
