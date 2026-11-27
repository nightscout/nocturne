import {
  FlagRuleComparison,
  FlagRuleSource,
  NotificationUrgency,
  type CustomReportFlagFact,
  type FlagRule,
  type FlagRulePreset,
  type TenantOverviewAttention,
  type TenantOverviewRuleReason,
  TenantOverviewAttentionKind,
} from "@nocturne/api-client";

function fact(
  dataset: string,
  name: string,
  label: string,
  unit: string | undefined,
  maxDays: number | undefined,
  range?: string
): CustomReportFlagFact {
  return {
    dataset,
    field: { name, label, kind: "number", unit, description: label, permitted: true, setting: false },
    kind: maxDays == null ? "live" : "period",
    range,
    scopes: [],
    maxDays,
  };
}

/** The facts the editor tests offer: a consensus and an own range figure, a glucose figure and a live fact. */
export const FLAG_FACTS: CustomReportFlagFact[] = [
  fact("glucose_statistics", "timeBelow", "Time below consensus range", "%", 90, "consensus"),
  fact("glucose_statistics", "timeBelowOwnRange", "Time below own range", "%", 90, "own"),
  fact("glucose_statistics", "mean", "Mean glucose", "mg/dL", 90),
  fact("current_status", "hoursSinceReading", "Hours since the last reading", "h", undefined),
];

export const FLAG_PRESETS: FlagRulePreset[] = [
  { dataset: "glucose_statistics", field: "timeBelow", days: 14, comparison: FlagRuleComparison.Above, value: 4 },
  { dataset: "current_status", field: "hoursSinceReading", days: undefined, comparison: FlagRuleComparison.Above, value: 2 },
];

export function makeRule(overrides: Partial<FlagRule> = {}): FlagRule {
  return {
    id: "00000000-0000-7000-8000-000000000001",
    dataset: "glucose_statistics",
    field: "timeBelow",
    days: 14,
    comparison: FlagRuleComparison.Above,
    value: 4,
    level: NotificationUrgency.Warn,
    createdAt: "2026-11-27T00:00:00Z",
    updatedAt: "2026-11-27T00:00:00Z",
    ...overrides,
  };
}

export function makeRuleReason(overrides: Partial<TenantOverviewRuleReason> = {}): TenantOverviewRuleReason {
  return {
    ruleId: "00000000-0000-7000-8000-000000000001",
    source: FlagRuleSource.Yours,
    dataset: "glucose_statistics",
    field: "timeBelow",
    label: "Time below consensus range",
    unit: "%",
    range: "consensus",
    value: 6.5,
    comparison: FlagRuleComparison.Above,
    ruleValue: 4,
    days: 14,
    from: "2026-11-13T00:00:00Z",
    to: "2026-11-27T00:00:00Z",
    ...overrides,
  };
}

export function ruleAttention(
  rule: TenantOverviewRuleReason,
  level: NotificationUrgency = NotificationUrgency.Warn
): TenantOverviewAttention {
  return { kind: TenantOverviewAttentionKind.FlagRule, level, rule };
}
