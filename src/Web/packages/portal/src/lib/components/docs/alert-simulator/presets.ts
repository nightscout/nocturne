import type { CompareOperator, ConditionNode, SimRule } from "./wire";

const below = (value: number): ConditionNode => ({ type: "threshold", threshold: { direction: "below", value } });
const above = (value: number): ConditionNode => ({ type: "threshold", threshold: { direction: "above", value } });
const falling = (rate: number): ConditionNode => ({ type: "rate_of_change", rate_of_change: { direction: "falling", rate } });
const sustained = (minutes: number, child: ConditionNode): ConditionNode => ({ type: "sustained", sustained: { minutes, child } });
const all = (...conditions: ConditionNode[]): ConditionNode => ({ type: "composite", composite: { operator: "and", conditions } });
const any = (...conditions: ConditionNode[]): ConditionNode => ({ type: "composite", composite: { operator: "or", conditions } });
const not = (child: ConditionNode): ConditionNode => ({ type: "not", not: { child } });
const between = (from: string, to: string): ConditionNode => ({ type: "time_of_day", time_of_day: { from, to } });
const cob = (operator: CompareOperator, value: number): ConditionNode => ({ type: "cob", cob: { operator, value } });
const iob = (operator: CompareOperator, value: number): ConditionNode => ({ type: "iob", iob: { operator, value } });

/** The example rules the docs pages load into the simulator, by id. Values are mg/dL, grams and units. */
export const PRESETS = {
	"low": { name: "Low", severity: "warning", condition: below(70) },
	"urgent-low": { name: "Urgent low", severity: "critical", condition: below(55) },
	"sustained-low": { name: "Low for 15 minutes", severity: "warning", condition: sustained(15, below(70)) },
	"falling-and-low": { name: "Falling toward low", severity: "warning", condition: all(below(100), falling(2)) },
	"low-or-falling": {
		name: "Low, or falling toward it",
		severity: "warning",
		condition: any(below(70), all(below(100), falling(2))),
	},
	"falling-any": { name: "Under 100 or falling", severity: "warning", condition: any(below(100), falling(2)) },
	"signal-loss": { name: "Signal loss", severity: "warning", condition: { type: "signal_loss", signal_loss: { timeout_minutes: 20 } } },
	"high": { name: "High", severity: "info", condition: above(250) },
	"high-auto-resolve": {
		name: "High, until it is coming down",
		severity: "info",
		condition: above(250),
		autoResolve: sustained(15, falling(1)),
	},
	"high-outside-dinner": {
		name: "High, outside dinner time",
		severity: "info",
		condition: all(sustained(30, above(180)), not(between("18:00", "20:00"))),
	},
	"low-untreated": {
		name: "Low, unless carbs are on board",
		severity: "warning",
		condition: all(below(70), not(cob(">=", 5))),
	},
	"high-no-insulin": {
		name: "High with no insulin on board",
		severity: "warning",
		condition: all(sustained(30, above(200)), iob("<", 0.5)),
	},
} satisfies Record<string, Omit<SimRule, "id">>;

export type PresetId = keyof typeof PRESETS;

export function presetRule(id: PresetId, index: number): SimRule {
	const preset: Omit<SimRule, "id"> = PRESETS[id];
	return {
		id: `00000000-0000-4000-8000-${String(index + 1).padStart(12, "0")}`,
		...structuredClone(preset),
	};
}
