import { AlertRuleSeverity } from "#lib/api/generated/nocturne-api-client.js";
import { bg, bgLabel, time } from "#lib/utils/formatting.js";
import { plural } from "#lib/utils/plural.js";
import { summarizeCondition } from "../summarizeCondition.svelte";
import { browserTimeZone, ensureCompositeRoot, nodeFromApi, type ConditionNode } from "../types";

/**
 * The alert types the wizard offers. Each one is a single condition with fixed
 * comparison, so the person sets a number and never an operator. `custom`
 * hands the whole condition to the rule builder.
 */
export const TEMPLATE_IDS = [
	"urgent_low",
	"low",
	"high",
	"falling_fast",
	"rising_fast",
	"no_readings",
	"urgent_low_soon",
	"reservoir_low",
	"pump_battery_low",
	"site_change_due",
	"sensor_change_due",
	"loop_not_running",
	"phone_battery_low",
	"custom",
] as const;

export type TemplateId = (typeof TEMPLATE_IDS)[number];

export interface TemplateDef {
	id: TemplateId;
	/** `common` types are listed first; `more` sit behind a disclosure. */
	group: "common" | "more";
	severity: AlertRuleSeverity;
	/** Whether "wait before alerting" applies; a type with its own duration has none. */
	canWait: boolean;
	build: () => ConditionNode | null;
}

export const TEMPLATES: readonly TemplateDef[] = [
	{
		id: "urgent_low",
		group: "common",
		severity: AlertRuleSeverity.Critical,
		canWait: true,
		build: () => ({ type: "threshold", threshold: { direction: "below", value: 55 } }),
	},
	{
		id: "low",
		group: "common",
		severity: AlertRuleSeverity.Warning,
		canWait: true,
		build: () => ({ type: "threshold", threshold: { direction: "below", value: 70 } }),
	},
	{
		id: "high",
		group: "common",
		severity: AlertRuleSeverity.Warning,
		canWait: true,
		build: () => ({ type: "threshold", threshold: { direction: "above", value: 250 } }),
	},
	{
		id: "falling_fast",
		group: "common",
		severity: AlertRuleSeverity.Warning,
		canWait: true,
		build: () => ({ type: "rate_of_change", rate_of_change: { direction: "falling", rate: 3 } }),
	},
	{
		id: "rising_fast",
		group: "common",
		severity: AlertRuleSeverity.Info,
		canWait: true,
		build: () => ({ type: "rate_of_change", rate_of_change: { direction: "rising", rate: 3 } }),
	},
	{
		id: "no_readings",
		group: "common",
		severity: AlertRuleSeverity.Warning,
		canWait: false,
		build: () => ({ type: "signal_loss", signal_loss: { timeout_minutes: 20 } }),
	},
	{
		id: "urgent_low_soon",
		group: "more",
		severity: AlertRuleSeverity.Critical,
		canWait: false,
		build: () => ({
			type: "predicted",
			predicted: { operator: "<=", value: 55, within_minutes: 20 },
		}),
	},
	{
		id: "reservoir_low",
		group: "more",
		severity: AlertRuleSeverity.Warning,
		canWait: false,
		build: () => ({ type: "reservoir", reservoir: { operator: "<=", value: 20 } }),
	},
	{
		id: "pump_battery_low",
		group: "more",
		severity: AlertRuleSeverity.Warning,
		canWait: false,
		build: () => ({ type: "pump_battery", pump_battery: { operator: "<=", value: 20 } }),
	},
	{
		id: "site_change_due",
		group: "more",
		severity: AlertRuleSeverity.Info,
		canWait: false,
		build: () => ({ type: "site_age", site_age: { operator: ">=", value: 72 } }),
	},
	{
		id: "sensor_change_due",
		group: "more",
		severity: AlertRuleSeverity.Info,
		canWait: false,
		build: () => ({ type: "sensor_age", sensor_age: { operator: ">=", value: 10 } }),
	},
	{
		id: "loop_not_running",
		group: "more",
		severity: AlertRuleSeverity.Warning,
		canWait: false,
		build: () => ({ type: "loop_stale", loop_stale: { operator: ">", minutes: 30 } }),
	},
	{
		id: "phone_battery_low",
		group: "more",
		severity: AlertRuleSeverity.Info,
		canWait: false,
		build: () => ({ type: "uploader_battery", uploader_battery: { operator: "<=", value: 20 } }),
	},
	{
		id: "custom",
		group: "more",
		severity: AlertRuleSeverity.Warning,
		canWait: false,
		build: () => null,
	},
];

export function getTemplate(id: TemplateId): TemplateDef {
	return TEMPLATES.find((t) => t.id === id) ?? TEMPLATES[0];
}

/** Built per call so wuchale reads them in the current locale. */
export function templateLabel(id: TemplateId): string {
	switch (id) {
		case "urgent_low":
			return "Urgent low";
		case "low":
			return "Low";
		case "high":
			return "High";
		case "falling_fast":
			return "Falling fast";
		case "rising_fast":
			return "Rising fast";
		case "no_readings":
			return "No readings";
		case "urgent_low_soon":
			return "Urgent low soon";
		case "reservoir_low":
			return "Pump reservoir low";
		case "pump_battery_low":
			return "Pump battery low";
		case "site_change_due":
			return "Site change due";
		case "sensor_change_due":
			return "Sensor change due";
		case "loop_not_running":
			return "Loop not running";
		case "phone_battery_low":
			return "Phone battery low";
		case "custom":
			return "Build your own";
	}
}

export function templateDescription(id: TemplateId): string {
	switch (id) {
		case "urgent_low":
			return "Glucose is below a level that needs action now.";
		case "low":
			return "Glucose is below the level you set.";
		case "high":
			return "Glucose is above the level you set.";
		case "falling_fast":
			return "Glucose decreases faster than the rate you set.";
		case "rising_fast":
			return "Glucose increases faster than the rate you set.";
		case "no_readings":
			return "The sensor sends no glucose readings.";
		case "urgent_low_soon":
			return "The forecast shows an urgent low in the next minutes.";
		case "reservoir_low":
			return "The pump has little insulin left.";
		case "pump_battery_low":
			return "The pump battery is almost empty.";
		case "site_change_due":
			return "The infusion site is older than the age you set.";
		case "sensor_change_due":
			return "The sensor is older than the age you set.";
		case "loop_not_running":
			return "The loop has not run for some time.";
		case "phone_battery_low":
			return "The battery of the phone that uploads the data is almost empty.";
		case "custom":
			return "Combine conditions with the rule builder.";
	}
}

/** What the person has chosen so far, before it becomes a rule. */
export interface AlertDraft {
	template: TemplateId;
	/** The one condition of a template; `null` for `custom`. */
	leaf: ConditionNode | null;
	/** Minutes the condition must stay true before the alert starts; 0 alerts at once. */
	waitMinutes: number;
	hours: { enabled: boolean; from: string; to: string };
	/** The rule builder's tree, for `custom`. */
	custom: ConditionNode | null;
}

export function newDraft(id: TemplateId): AlertDraft {
	const template = getTemplate(id);
	return {
		template: id,
		leaf: template.build(),
		waitMinutes: 0,
		hours: { enabled: false, from: "22:00", to: "07:00" },
		custom:
			id === "custom"
				? ensureCompositeRoot(nodeFromApi("threshold", { direction: "below", value: 70 })!)
				: null,
	};
}

/**
 * The rule condition a draft stands for: its condition, held for the wait when
 * one is set, and limited to the chosen hours.
 */
export function draftCondition(draft: AlertDraft): ConditionNode | null {
	if (draft.template === "custom") return draft.custom;
	if (!draft.leaf) return null;

	const waits = getTemplate(draft.template).canWait && draft.waitMinutes > 0;
	const main: ConditionNode = waits
		? { type: "sustained", sustained: { minutes: draft.waitMinutes, child: draft.leaf } }
		: draft.leaf;
	const conditions = draft.hours.enabled
		? [
				main,
				{
					type: "time_of_day" as const,
					time_of_day: {
						from: draft.hours.from,
						to: draft.hours.to,
						timezone: browserTimeZone(),
					},
				},
			]
		: [main];
	return { type: "composite", composite: { operator: "and", conditions } };
}

const MIN_GLUCOSE_MGDL = 40;
const MAX_GLUCOSE_MGDL = 400;

/** Why the draft cannot be used yet, or `null` when it can. */
export function draftIssue(draft: AlertDraft): string | null {
	if (draft.template === "custom") {
		return draft.custom?.composite?.conditions.length ? null : "Add at least one condition.";
	}
	const leaf = draft.leaf;
	if (!leaf) return "Choose an alert type.";

	const glucose = leaf.threshold?.value ?? leaf.predicted?.value;
	if (glucose !== undefined && !(glucose >= MIN_GLUCOSE_MGDL && glucose <= MAX_GLUCOSE_MGDL)) {
		return `Enter a glucose level between ${bg(MIN_GLUCOSE_MGDL)} and ${bg(MAX_GLUCOSE_MGDL)} ${bgLabel()}.`;
	}
	if (leaf.rate_of_change && !((leaf.rate_of_change.rate ?? 0) > 0)) {
		return "Enter a rate above 0.";
	}
	const minutes =
		leaf.signal_loss?.timeout_minutes ?? leaf.predicted?.within_minutes ?? leaf.loop_stale?.minutes;
	if (minutes !== undefined && !(minutes >= 1)) return "Enter 1 minute or more.";

	const amount =
		leaf.reservoir?.value ??
		leaf.pump_battery?.value ??
		leaf.uploader_battery?.value ??
		leaf.site_age?.value ??
		leaf.sensor_age?.value;
	if (amount !== undefined && !(amount > 0)) return "Enter a number above 0.";

	if (draft.hours.enabled) {
		if (!draft.hours.from || !draft.hours.to) return "Enter a start time and an end time.";
		if (draft.hours.from === draft.hours.to) return "The start time and the end time must be different.";
	}
	return null;
}

const MAX_NAME_LENGTH = 80;

/**
 * The name the alert gets when the person does not type one. It is the text a
 * notification shows, so it says the type and the level that started it.
 */
export function suggestName(draft: AlertDraft): string {
	const name = withQualifiers(baseName(draft), draft);
	return name.length > MAX_NAME_LENGTH ? `${name.slice(0, MAX_NAME_LENGTH - 1).trimEnd()}…` : name;
}

function baseName(draft: AlertDraft): string {
	const leaf = draft.leaf;
	switch (draft.template) {
		case "urgent_low":
			return `Urgent low: below ${bg(leaf?.threshold?.value ?? 0)} ${bgLabel()}`;
		case "low":
			return `Low: below ${bg(leaf?.threshold?.value ?? 0)} ${bgLabel()}`;
		case "high":
			return `High: above ${bg(leaf?.threshold?.value ?? 0)} ${bgLabel()}`;
		case "falling_fast":
			return `Falling fast: ${bg(leaf?.rate_of_change?.rate ?? 0)} ${bgLabel()} per minute`;
		case "rising_fast":
			return `Rising fast: ${bg(leaf?.rate_of_change?.rate ?? 0)} ${bgLabel()} per minute`;
		case "no_readings":
			return `No readings for ${minutesText(leaf?.signal_loss?.timeout_minutes ?? 0)}`;
		case "urgent_low_soon":
			return `Urgent low soon: below ${bg(leaf?.predicted?.value ?? 0)} ${bgLabel()} in ${minutesText(leaf?.predicted?.within_minutes ?? 0)}`;
		case "reservoir_low":
			return `Pump reservoir low: ${leaf?.reservoir?.value ?? 0} U`;
		case "pump_battery_low":
			return `Pump battery low: ${leaf?.pump_battery?.value ?? 0}%`;
		case "site_change_due":
			return `Site change due: ${daysText((leaf?.site_age?.value ?? 0) / 24)}`;
		case "sensor_change_due":
			return `Sensor change due: ${daysText(leaf?.sensor_age?.value ?? 0)}`;
		case "loop_not_running":
			return `Loop not running for ${minutesText(leaf?.loop_stale?.minutes ?? 0)}`;
		case "phone_battery_low":
			return `Phone battery low: ${leaf?.uploader_battery?.value ?? 0}%`;
		case "custom":
			return summarizeCondition(draft.custom) || "Custom alert";
	}
}

function withQualifiers(name: string, draft: AlertDraft): string {
	if (draft.template === "custom") return name;
	const waited =
		getTemplate(draft.template).canWait && draft.waitMinutes > 0
			? `${name} for ${minutesText(draft.waitMinutes)}`
			: name;
	return draft.hours.enabled
		? `${waited}, ${clockText(draft.hours.from)} to ${clockText(draft.hours.to)}`
		: waited;
}

/** An "HH:mm" time of day in the person's time format. */
function clockText(hhmm: string): string {
	const [hours, minutes] = hhmm.split(":").map(Number);
	if (!Number.isFinite(hours) || !Number.isFinite(minutes)) return hhmm;
	return time(new Date(2000, 0, 1, hours, minutes));
}

export function minutesText(minutes: number): string {
	if (minutes >= 60 && minutes % 60 === 0) return plural(minutes / 60, ["# hour", "# hours"]);
	return plural(minutes, ["# minute", "# minutes"]);
}

export function daysText(days: number): string {
	return plural(days, ["# day", "# days"]);
}
