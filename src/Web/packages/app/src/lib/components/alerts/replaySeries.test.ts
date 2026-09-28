import { describe, expect, it } from "vitest";
import { legendForRules } from "./replaySeries";
import type { ConditionNode } from "./types";

const below70: ConditionNode = { type: "threshold", threshold: { direction: "below", value: 70 } };
const cob: ConditionNode = { type: "cob", cob: { operator: ">=", value: 5 } };
const iob: ConditionNode = { type: "iob", iob: { operator: "<", value: 0.5 } };

describe("legendForRules", () => {
	it("draws glucose alone for a glucose-only rule", () => {
		const legend = legendForRules([below70]);
		expect([legend.iob, legend.cob, legend.basal, legend.bolus, legend.carbs]).toEqual([
			false,
			false,
			false,
			false,
			false,
		]);
	});

	it("draws carbs on board and carb markers for a carbs leaf, however deeply wrapped", () => {
		const tree: ConditionNode = {
			type: "composite",
			composite: { operator: "and", conditions: [below70, { type: "not", not: { child: cob } }] },
		};
		const legend = legendForRules([tree]);
		expect(legend).toMatchObject({ cob: true, carbs: true, iob: false, bolus: false });
	});

	it("takes the union across rules", () => {
		const legend = legendForRules([cob, { type: "sustained", sustained: { minutes: 15, child: iob } }]);
		expect(legend).toMatchObject({ cob: true, carbs: true, iob: true, bolus: true, basal: false });
	});
});
