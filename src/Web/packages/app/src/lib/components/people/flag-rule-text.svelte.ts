import {
  FlagRuleComparison,
  FlagRuleSource,
  type FlagRule,
  type TenantOverviewRuleReason,
} from "@nocturne/api-client";
import { bg, bgLabel, bgRange, formatLocale } from "#lib/utils/formatting.js";
import { plural } from "#lib/utils/plural.js";

/** The unit the API gives every glucose fact and rule value in. */
export const GLUCOSE_FACT_UNIT = "mg/dL";

/** A fact's value as the viewer reads it: glucose in the viewer's unit, anything else in the fact's own unit. */
export function factValue(value: number, unit: string | null | undefined): string {
  if (unit === GLUCOSE_FACT_UNIT) return `${bg(value)} ${bgLabel()}`;
  const figure = value.toLocaleString(formatLocale(), { maximumFractionDigits: 1 });
  if (!unit) return figure;
  return unit === "%" ? `${figure}%` : `${figure} ${unit}`;
}

function periodText(days: number): string {
  return plural(days, ["# day", "# days"]);
}

/** What a person's figure is, and the rule it met, the viewer's own or a team's. A fact, never advice. */
export function ruleReasonPhrase(rule: TenantOverviewRuleReason): string {
  const label = rule.label ?? "";
  const value = factValue(rule.value ?? 0, rule.unit);
  const threshold = factValue(rule.ruleValue ?? 0, rule.unit);
  const below = rule.comparison === FlagRuleComparison.Below;
  const team = rule.source === FlagRuleSource.Team;
  if (rule.days == null) {
    if (team)
      return below
        ? `${label}: ${value} (team rule: below ${threshold})`
        : `${label}: ${value} (team rule: above ${threshold})`;
    return below
      ? `${label}: ${value} (your rule: below ${threshold})`
      : `${label}: ${value} (your rule: above ${threshold})`;
  }
  const period = periodText(rule.days);
  if (team)
    return below
      ? `${label}: ${value} in the last ${period} (team rule: below ${threshold})`
      : `${label}: ${value} in the last ${period} (team rule: above ${threshold})`;
  return below
    ? `${label}: ${value} in the last ${period} (your rule: below ${threshold})`
    : `${label}: ${value} in the last ${period} (your rule: above ${threshold})`;
}

/** The detail a rule's reason states beside its phrase: the own range it was measured against, and low coverage. */
export function ruleReasonDetails(rule: TenantOverviewRuleReason): string[] {
  const details: string[] = [];
  const bound = rule.bound;
  if (bound?.low != null && bound.high != null) {
    const range = bgRange(bound.low, bound.high);
    details.push(bound.variesOverDay ? `Own range ${range}, varies by time of day` : `Own range ${range}`);
  }
  if (rule.coverage != null) {
    // Coverage is stated only below the sufficiency mark, so it must never round up to read as the mark.
    const worn = Math.floor(rule.coverage).toLocaleString(formatLocale());
    details.push(`Sensor worn ${worn}% of the time`);
  }
  return details;
}

/** A rule, or a data filter, as a sentence. `label` is the fact's label. */
export function ruleSentence(
  rule: Pick<FlagRule, "days" | "comparison" | "value">,
  label: string,
  unit: string | null | undefined
): string {
  const value = factValue(rule.value ?? 0, unit);
  const below = rule.comparison === FlagRuleComparison.Below;
  if (rule.days == null) {
    return below ? `${label} is below ${value}` : `${label} is above ${value}`;
  }
  const period = periodText(rule.days);
  return below
    ? `${label} in the last ${period} is below ${value}`
    : `${label} in the last ${period} is above ${value}`;
}
