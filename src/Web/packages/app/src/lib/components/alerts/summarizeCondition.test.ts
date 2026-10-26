import { describe, it, expect } from "vitest";
import { AlertWeekday, defaultPayload, GlucoseBucket, PumpModeState, StateSpanCategory, type ConditionNode } from "./types";
import { alertWeekdayLabel, summarizeCondition } from "./summarizeCondition.svelte";
import { LEAF_FACTS } from "./factCatalog";
import { glucoseUnits } from "#lib/stores/appearance-store.svelte.js";

describe("summarizeCondition", () => {
	it("returns a non-empty string for every fact-catalog leaf kind", () => {
		// Regression guard: every leaf the editor can create must render something
		// in the rule sidebar. Returning undefined would render as the literal
		// string "undefined" in the sidebar disclosure body.
		for (const fact of LEAF_FACTS) {
			const node = defaultPayload(fact.kind);
			const summary = summarizeCondition(node);
			expect(summary, `summarizeCondition for kind=${fact.kind}`).toBeTruthy();
		}
	});


	it("renders day-of-week days by name", () => {
		const node: ConditionNode = {
			type: "day_of_week",
			day_of_week: { days: [AlertWeekday.Saturday, AlertWeekday.Sunday] },
		};
		expect(summarizeCondition(node)).toBe("On Sat or Sun");
	});

	it("starts every leaf chip with an uppercase letter", () => {
		for (const fact of LEAF_FACTS) {
			const summary = summarizeCondition(defaultPayload(fact.kind));
			expect(summary, `kind=${fact.kind}`).toMatch(/^\p{Lu}/u);
		}
	});

	it("renders every pump mode as one sentence-case phrase, active and not", () => {
		for (const mode of Object.values(PumpModeState)) {
			for (const isActive of [true, false]) {
				const chip = summarizeCondition({ type: "pump_state", pump_state: { mode, is_active: isActive } });
				expect(chip, `mode=${mode} active=${isActive}`).toMatch(/^Pump (not )?(in (automatic|manual|sleep|exercise) mode|in Liberty mode|limited|boosting|easing off|suspended|off)$/);
			}
		}
	});

	it("keeps pump mode names lowercase except the named Liberty mode", () => {
		const chip = (mode: PumpModeState): string =>
			summarizeCondition({ type: "pump_state", pump_state: { mode, is_active: true } });
		expect(chip(PumpModeState.Automatic)).toBe("Pump in automatic mode");
		expect(chip(PumpModeState.Manual)).toBe("Pump in manual mode");
		expect(chip(PumpModeState.Sleep)).toBe("Pump in sleep mode");
		expect(chip(PumpModeState.Exercise)).toBe("Pump in exercise mode");
		expect(chip(PumpModeState.Liberty)).toBe("Pump in Liberty mode");
	});

	it("renders a boosting pump and its negation", () => {
		const boosting = (isActive: boolean): ConditionNode => ({
			type: "pump_state",
			pump_state: { mode: PumpModeState.Boost, is_active: isActive },
		});
		expect(summarizeCondition(boosting(true))).toBe("Pump boosting");
		expect(summarizeCondition(boosting(false))).toBe("Pump not boosting");
	});

	it("adds the duration to a pump mode", () => {
		const node: ConditionNode = {
			type: "pump_state",
			pump_state: { mode: PumpModeState.Boost, is_active: true, for_minutes: 90 },
		};
		expect(summarizeCondition(node)).toBe("Pump boosting for 90 minutes");
	});

	it("renders rate of change by direction", () => {
		const chip = (direction: string): ConditionNode => ({
			type: "rate_of_change",
			rate_of_change: { direction, rate: 3 },
		});
		expect(summarizeCondition(chip("falling"))).toBe("Glucose falling at least 3 mg/dL per minute");
		expect(summarizeCondition(chip("rising"))).toBe("Glucose rising at least 3 mg/dL per minute");
	});

	it("joins composite OR with the word or", () => {
		const node: ConditionNode = {
			type: "composite",
			composite: {
				operator: "or",
				conditions: [defaultPayload("threshold"), { type: "trend", trend: { bucket: "rising_fast" } }],
			},
		};
		expect(summarizeCondition(node)).toBe("Glucose below 70 mg/dL or Trend rising fast");
	});

	it("returns empty string for null/undefined input", () => {
		expect(summarizeCondition(null)).toBe("");
		expect(summarizeCondition(undefined)).toBe("");
	});

	it("renders a simple threshold below", () => {
		expect(summarizeCondition(defaultPayload("threshold"))).toBe("Glucose below 70 mg/dL");
	});

	it("renders a threshold above", () => {
		const node: ConditionNode = {
			type: "threshold",
			threshold: { direction: "above", value: 200 },
		};
		expect(summarizeCondition(node)).toBe("Glucose above 200 mg/dL");
	});

	it("uses mmol/L when configured", () => {
		glucoseUnits.current = "mmol";
		try {
			expect(summarizeCondition(defaultPayload("threshold"))).toBe("Glucose below 3.9 mmol/L");
		} finally {
			glucoseUnits.current = "mg/dl";
		}
	});

	it("renders predicted with horizon", () => {
		expect(summarizeCondition(defaultPayload("predicted"))).toBe(
			"Predicted glucose ≤ 70 mg/dL in 30 minutes",
		);
	});

	it("renders trend buckets", () => {
		const node: ConditionNode = {
			type: "trend",
			trend: { bucket: "falling_fast" },
		};
		expect(summarizeCondition(node)).toBe("Trend falling fast");
	});

	it("renders time-of-day window", () => {
		const node: ConditionNode = {
			type: "time_of_day",
			time_of_day: { from: "22:00", to: "06:00" },
		};
		expect(summarizeCondition(node)).toBe("Between 22:00 and 06:00");
	});

	it("renders sustained as suffix", () => {
		const node: ConditionNode = {
			type: "sustained",
			sustained: { minutes: 15, child: defaultPayload("threshold") },
		};
		expect(summarizeCondition(node)).toBe("Glucose below 70 mg/dL for 15 minutes");
	});

	it("formats hour-aligned minute durations as hours", () => {
		const node: ConditionNode = {
			type: "sustained",
			sustained: { minutes: 120, child: defaultPayload("threshold") },
		};
		expect(summarizeCondition(node)).toBe("Glucose below 70 mg/dL for 2 hours");
	});

	it("renders day-aligned site age in days", () => {
		const node: ConditionNode = {
			type: "site_age",
			site_age: { operator: ">=", value: 72 },
		};
		expect(summarizeCondition(node)).toBe("Site age ≥ 3 days");
	});

	it("joins composite AND with the word and", () => {
		const node: ConditionNode = {
			type: "composite",
			composite: {
				operator: "and",
				conditions: [
					defaultPayload("threshold"),
					{ type: "trend", trend: { bucket: "falling" } },
				],
			},
		};
		expect(summarizeCondition(node)).toBe("Glucose below 70 mg/dL and Trend falling");
	});

	it("brackets a nested OR group inside an outer AND", () => {
		const node: ConditionNode = {
			type: "composite",
			composite: {
				operator: "and",
				conditions: [
					{
						type: "composite",
						composite: {
							operator: "or",
							conditions: [
								defaultPayload("threshold"),
								{ type: "trend", trend: { bucket: "falling" } },
							],
						},
					},
					defaultPayload("predicted"),
				],
			},
		};
		expect(summarizeCondition(node)).toBe(
			"(Glucose below 70 mg/dL or Trend falling) and Predicted glucose ≤ 70 mg/dL in 30 minutes",
		);
	});

	it("does not bracket a same-operator nested group", () => {
		const node: ConditionNode = {
			type: "composite",
			composite: {
				operator: "and",
				conditions: [
					{
						type: "composite",
						composite: {
							operator: "and",
							conditions: [
								{ type: "iob", iob: { operator: ">=", value: 1 } },
								{ type: "cob", cob: { operator: ">=", value: 10 } },
							],
						},
					},
					defaultPayload("threshold"),
				],
			},
		};
		expect(summarizeCondition(node)).toBe(
			"Insulin on board ≥ 1 U and Carbs on board ≥ 10 g and Glucose below 70 mg/dL",
		);
	});

	it("wraps NOT with parentheses", () => {
		const node: ConditionNode = {
			type: "not",
			not: { child: defaultPayload("threshold") },
		};
		expect(summarizeCondition(node)).toBe("Not (Glucose below 70 mg/dL)");
	});

	it("renders alert_state with a name resolver", () => {
		const node: ConditionNode = {
			type: "alert_state",
			alert_state: { alert_id: "abc-123-def", state: "firing", for_minutes: 2 },
		};
		expect(
			summarizeCondition(node, { resolveAlertName: () => "Approaching Low" }),
		).toBe("Alert Approaching Low firing for 2 minutes");
	});

	it("falls back to a short id when no resolver is provided", () => {
		const node: ConditionNode = {
			type: "alert_state",
			alert_state: { alert_id: "01234567-89ab-cdef-0123-456789abcdef", state: "firing" },
		};
		expect(summarizeCondition(node)).toBe("Alert abcdef firing");
	});

	it("renders tracker age with hour-aligned minutes as hours", () => {
		const node: ConditionNode = {
			type: "tracker_age",
			tracker_age: {
				tracker_definition_id: "abc-123",
				operator: ">=",
				minutes: 2880,
			},
		};
		expect(summarizeCondition(node)).toBe("Tracker age ≥ 48 hours");
	});

	it("keeps the sign on a negative tracker age", () => {
		const node: ConditionNode = {
			type: "tracker_age",
			tracker_age: {
				tracker_definition_id: "abc-123",
				operator: "<=",
				minutes: -120,
			},
		};
		expect(summarizeCondition(node)).toBe("Tracker age ≤ -2 hours");
	});

	it("renders DND with sustained duration", () => {
		const node: ConditionNode = {
			type: "do_not_disturb",
			do_not_disturb: { is_active: true, for_minutes: 30 },
		};
		expect(summarizeCondition(node)).toBe("Do not disturb on for 30 minutes");
	});

	it("renders a pump mode from the wire enum member, not the raw value", () => {
		const node: ConditionNode = {
			type: "pump_state",
			pump_state: { mode: PumpModeState.EaseOff, is_active: true },
		};
		expect(summarizeCondition(node)).toBe("Pump easing off");
	});

	it("renders a negated pump mode from the wire enum member", () => {
		const node: ConditionNode = {
			type: "pump_state",
			pump_state: { mode: PumpModeState.Liberty, is_active: false },
		};
		expect(summarizeCondition(node)).toBe("Pump not in Liberty mode");
	});

	it("collapses a composite with a single child to that child", () => {
		const node: ConditionNode = {
			type: "composite",
			composite: { operator: "and", conditions: [defaultPayload("threshold")] },
		};
		expect(summarizeCondition(node)).toBe("Glucose below 70 mg/dL");
	});

	it("names every glucose bucket in a bucket condition", () => {
		const node: ConditionNode = {
			type: "glucose_bucket",
			glucose_bucket: {
				buckets: [
					GlucoseBucket.VeryLow,
					GlucoseBucket.Low,
					GlucoseBucket.TightRange,
					GlucoseBucket.InRange,
					GlucoseBucket.High,
					GlucoseBucket.VeryHigh,
				],
			},
		};
		expect(summarizeCondition(node)).toBe(
			"Glucose range: Very low, Low, Tight range, In range, High, or Very high",
		);
	});

	it("gives wuchale a chip it can extract for every state of every leaf", () => {
		const both = [true, false];
		const stateSpans = Object.values(StateSpanCategory).flatMap((category) =>
			both.map((is_active): ConditionNode => ({ type: "state_span_active", state_span_active: { category, is_active } })),
		);
		const pumpModes = Object.values(PumpModeState).flatMap((mode) =>
			both.map((is_active): ConditionNode => ({ type: "pump_state", pump_state: { mode, is_active } })),
		);
		const alertStates = ["firing", "unacknowledged", "acknowledged", "other"].map(
			(state): ConditionNode => ({ type: "alert_state", alert_state: { alert_id: "abc-123", state } }),
		);
		const trends = ["falling_fast", "falling", "flat", "rising", "rising_fast", "other"].map(
			(bucket): ConditionNode => ({ type: "trend", trend: { bucket } }),
		);
		const sleep = both.map(
			(is_active): ConditionNode => ({ type: "sleep_session_active", sleep_session_active: { is_active } }),
		);
		const buckets = Object.values(GlucoseBucket).map(
			(bucket): ConditionNode => ({ type: "glucose_bucket", glucose_bucket: { buckets: [bucket] } }),
		);
		const defaults = LEAF_FACTS.map((fact) => defaultPayload(fact.kind));
		const nodes = [...defaults, ...stateSpans, ...pumpModes, ...alertStates, ...trends, ...sleep, ...buckets];
		for (const node of nodes) {
			const chip = summarizeCondition(node);
			expect(chip, JSON.stringify(node)).toMatch(/^\p{Lu}/u);
			expect(chip, JSON.stringify(node)).not.toMatch(/^([A-Z]|\P{L})+$/u);
		}
	});
});

describe("alertWeekdayLabel", () => {
	it("returns a day the enum does not hold as it came", () => {
		expect(alertWeekdayLabel("someday" as AlertWeekday)).toBe("someday");
	});
});
