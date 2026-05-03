import { describe, it, expect } from "vitest";
import { defaultPayload, type ConditionNode } from "./types";
import { suggestAutoResolve } from "./suggestAutoResolve";

describe("suggestAutoResolve", () => {
	it("returns null for null input", () => {
		expect(suggestAutoResolve(null)).toBeNull();
	});

	it("inverts a threshold below into a threshold above and wraps in AND", () => {
		const result = suggestAutoResolve({
			type: "threshold",
			threshold: { direction: "below", value: 70 },
		});
		expect(result?.type).toBe("composite");
		const inner = result?.composite?.conditions[0];
		expect(inner?.type).toBe("threshold");
		expect(inner?.threshold?.direction).toBe("above");
		expect(inner?.threshold?.value).toBe(70);
	});

	it("flips an AND of leaves into an OR of inverted leaves", () => {
		const firing: ConditionNode = {
			type: "composite",
			composite: {
				operator: "and",
				conditions: [
					{ type: "threshold", threshold: { direction: "below", value: 70 } },
					{ type: "iob", iob: { operator: ">=", value: 1 } },
				],
			},
		};
		const result = suggestAutoResolve(firing);
		expect(result?.type).toBe("composite");
		expect(result?.composite?.operator).toBe("or");
		expect(result?.composite?.conditions).toHaveLength(2);
		expect(result?.composite?.conditions[0].threshold?.direction).toBe("above");
		expect(result?.composite?.conditions[1].iob?.operator).toBe("<");
	});

	it("flips an OR of leaves into an AND of inverted leaves", () => {
		const firing: ConditionNode = {
			type: "composite",
			composite: {
				operator: "or",
				conditions: [
					{ type: "iob", iob: { operator: ">=", value: 1 } },
					{ type: "cob", cob: { operator: ">=", value: 10 } },
				],
			},
		};
		const result = suggestAutoResolve(firing);
		expect(result?.composite?.operator).toBe("and");
	});

	it("strips a sustained wrapper before inverting the inner predicate", () => {
		const firing: ConditionNode = {
			type: "sustained",
			sustained: {
				minutes: 15,
				child: defaultPayload("threshold"),
			},
		};
		const result = suggestAutoResolve(firing);
		const inner = result?.composite?.conditions[0];
		expect(inner?.type).toBe("threshold");
		expect(inner?.threshold?.direction).toBe("above");
	});

	it("flips pump-suspended is_active to false", () => {
		const firing: ConditionNode = {
			type: "pump_suspended",
			pump_suspended: { is_active: true, for_minutes: 15 },
		};
		const result = suggestAutoResolve(firing);
		const inner = result?.composite?.conditions[0];
		expect(inner?.type).toBe("pump_suspended");
		expect(inner?.pump_suspended?.is_active).toBe(false);
	});

	it("inverts alert_state firing to acknowledged", () => {
		const firing: ConditionNode = {
			type: "alert_state",
			alert_state: { alert_id: "abc", state: "firing" },
		};
		const result = suggestAutoResolve(firing);
		const inner = result?.composite?.conditions[0];
		expect(inner?.alert_state?.state).toBe("acknowledged");
	});

	it("returns null for time_of_day (no clean inversion)", () => {
		const firing: ConditionNode = {
			type: "time_of_day",
			time_of_day: { from: "22:00", to: "06:00" },
		};
		expect(suggestAutoResolve(firing)).toBeNull();
	});

	it("returns null for loop_stale (would cross-type into BG staleness)", () => {
		const firing: ConditionNode = {
			type: "loop_stale",
			loop_stale: { operator: ">", minutes: 15 },
		};
		expect(suggestAutoResolve(firing)).toBeNull();
	});
});
