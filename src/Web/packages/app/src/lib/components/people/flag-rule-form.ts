import {
  FlagRuleComparison,
  NotificationUrgency,
  type CustomReportFlagFact,
  type FlagRule,
  type FlagRulePreset,
  type FlagRuleRequest,
} from "@nocturne/api-client";
import { convertFromDisplayUnits, convertToDisplayUnits } from "@nocturne/ui/glucose";
import type { GlucoseUnits } from "#lib/stores/appearance-store.svelte.js";
import { GLUCOSE_FACT_UNIT } from "./flag-rule-text.svelte.js";

/** The levels a rule may give, most quiet first. */
export const RULE_LEVELS = [
  NotificationUrgency.Info,
  NotificationUrgency.Warn,
  NotificationUrgency.Hazard,
] as const;

export const DEFAULT_RULE_LEVEL = NotificationUrgency.Warn;

/** A rule as the editor holds it: its value in the unit the viewer reads the fact in. */
export interface RuleDraft {
  dataset: string;
  field: string;
  /** The last N days of a period fact; null for a fact as it is now. */
  days: number | null;
  comparison: FlagRuleComparison;
  value: number;
  level: NotificationUrgency;
}

export function factKey(dataset: string | undefined, field: string | undefined): string {
  return `${dataset ?? ""}.${field ?? ""}`;
}

export function findFact(
  facts: CustomReportFlagFact[],
  dataset: string | undefined,
  field: string | undefined
): CustomReportFlagFact | undefined {
  return facts.find((fact) => fact.dataset === dataset && fact.field.name === field);
}

export function isPeriodFact(fact: CustomReportFlagFact): boolean {
  return fact.maxDays != null;
}

/** A value in the fact's unit as the viewer enters it: glucose in the viewer's unit. */
export function toDisplayValue(value: number, fact: CustomReportFlagFact, units: GlucoseUnits): number {
  return fact.field.unit === GLUCOSE_FACT_UNIT ? convertToDisplayUnits(value, units) : value;
}

/** A value the viewer entered, in the fact's unit: glucose in mg/dL. */
export function fromDisplayValue(value: number, fact: CustomReportFlagFact, units: GlucoseUnits): number {
  return fact.field.unit === GLUCOSE_FACT_UNIT ? convertFromDisplayUnits(value, units) : value;
}

export function draftOfPreset(
  preset: FlagRulePreset,
  fact: CustomReportFlagFact,
  units: GlucoseUnits
): RuleDraft {
  return {
    dataset: preset.dataset,
    field: preset.field,
    days: preset.days ?? null,
    comparison: preset.comparison,
    value: toDisplayValue(preset.value, fact, units),
    level: DEFAULT_RULE_LEVEL,
  };
}

/** A new draft for a fact chosen from the full list: a period fact starts on 14 days. */
export function draftOfFact(fact: CustomReportFlagFact): RuleDraft {
  return {
    dataset: fact.dataset,
    field: fact.field.name,
    days: isPeriodFact(fact) ? 14 : null,
    comparison: FlagRuleComparison.Above,
    value: 0,
    level: DEFAULT_RULE_LEVEL,
  };
}

export function draftOfRule(rule: FlagRule, fact: CustomReportFlagFact, units: GlucoseUnits): RuleDraft {
  return {
    dataset: rule.dataset ?? fact.dataset,
    field: rule.field ?? fact.field.name,
    days: rule.days ?? null,
    comparison: rule.comparison ?? FlagRuleComparison.Above,
    value: toDisplayValue(rule.value ?? 0, fact, units),
    level: rule.level ?? DEFAULT_RULE_LEVEL,
  };
}

/** The body the API takes for a draft: the value in the fact's unit, and no range for a fact as it is now. */
export function requestOf(draft: RuleDraft, fact: CustomReportFlagFact, units: GlucoseUnits): FlagRuleRequest {
  return {
    dataset: draft.dataset,
    field: draft.field,
    days: isPeriodFact(fact) ? (draft.days ?? undefined) : undefined,
    comparison: draft.comparison,
    value: fromDisplayValue(draft.value, fact, units),
    level: draft.level,
  };
}
