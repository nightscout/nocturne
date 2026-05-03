import { describe, it, expect } from "vitest";
import { defaultPayload, type ConditionNode } from "./types";
import { summarizeCondition } from "./summarizeCondition";
import { LEAF_FACTS } from "./factCatalog";

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


	it("returns empty string for null/undefined input", () => {
		expect(summarizeCondition(null)).toBe("");
		expect(summarizeCondition(undefined)).toBe("");
	});

	it("renders a simple threshold below", () => {
		expect(summarizeCondition(defaultPayload("threshold"))).toBe("BG < 70 mg/dL");
	});

	it("renders a threshold above", () => {
		const node: ConditionNode = {
			type: "threshold",
			threshold: { direction: "above", value: 200 },
		};
		expect(summarizeCondition(node)).toBe("BG > 200 mg/dL");
	});

	it("uses mmol/L when configured", () => {
		expect(summarizeCondition(defaultPayload("threshold"), { glucoseUnit: "mmol/L" })).toBe(
			"BG < 70 mmol/L",
		);
	});

	it("renders predicted with horizon", () => {
		expect(summarizeCondition(defaultPayload("predicted"))).toBe(
			"Predicted BG ≤ 70 mg/dL in 30m",
		);
	});

	it("renders trend buckets", () => {
		const node: ConditionNode = {
			type: "trend",
			trend: { bucket: "falling_fast" },
		};
		expect(summarizeCondition(node)).toBe("Trend: falling fast");
	});

	it("renders time-of-day window", () => {
		const node: ConditionNode = {
			type: "time_of_day",
			time_of_day: { from: "22:00", to: "06:00" },
		};
		expect(summarizeCondition(node)).toBe("between 22:00 and 06:00");
	});

	it("renders sustained as suffix", () => {
		const node: ConditionNode = {
			type: "sustained",
			sustained: { minutes: 15, child: defaultPayload("threshold") },
		};
		expect(summarizeCondition(node)).toBe("BG < 70 mg/dL for 15m");
	});

	it("formats hour-aligned minute durations as hours", () => {
		const node: ConditionNode = {
			type: "sustained",
			sustained: { minutes: 120, child: defaultPayload("threshold") },
		};
		expect(summarizeCondition(node)).toBe("BG < 70 mg/dL for 2h");
	});

	it("renders day-aligned site age in days", () => {
		const node: ConditionNode = {
			type: "site_age",
			site_age: { operator: ">=", value: 72 },
		};
		expect(summarizeCondition(node)).toBe("Site age ≥ 3d");
	});

	it("joins composite AND with the AND keyword", () => {
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
		expect(summarizeCondition(node)).toBe("BG < 70 mg/dL AND Trend: falling");
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
			"(BG < 70 mg/dL OR Trend: falling) AND Predicted BG ≤ 70 mg/dL in 30m",
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
			"IOB ≥ 1 U AND COB ≥ 10 g AND BG < 70 mg/dL",
		);
	});

	it("wraps NOT with parentheses", () => {
		const node: ConditionNode = {
			type: "not",
			not: { child: defaultPayload("threshold") },
		};
		expect(summarizeCondition(node)).toBe("not (BG < 70 mg/dL)");
	});

	it("renders alert_state with a name resolver", () => {
		const node: ConditionNode = {
			type: "alert_state",
			alert_state: { alert_id: "abc-123-def", state: "firing", for_minutes: 2 },
		};
		expect(
			summarizeCondition(node, { resolveAlertName: () => "Approaching Low" }),
		).toBe("Approaching Low firing for 2m");
	});

	it("falls back to a short id when no resolver is provided", () => {
		const node: ConditionNode = {
			type: "alert_state",
			alert_state: { alert_id: "01234567-89ab-cdef-0123-456789abcdef", state: "firing" },
		};
		expect(summarizeCondition(node)).toBe("alert abcdef firing");
	});

	it("renders DND with sustained duration", () => {
		const node: ConditionNode = {
			type: "do_not_disturb",
			do_not_disturb: { is_active: true, for_minutes: 30 },
		};
		expect(summarizeCondition(node)).toBe("Do Not Disturb on for 30m");
	});

	it("collapses a composite with a single child to that child", () => {
		const node: ConditionNode = {
			type: "composite",
			composite: { operator: "and", conditions: [defaultPayload("threshold")] },
		};
		expect(summarizeCondition(node)).toBe("BG < 70 mg/dL");
	});
});
