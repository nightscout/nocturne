import { describe, it, expect } from "vitest";
import type { AlertRuleResponse, LeafTransitionLog as ApiLeafTransitionLog } from "$api-clients";
import { assignLeafIds, LeafTransitionLog, composeRuleTruth } from "./leafEval";
import { nodeFromApi, type ConditionNode } from "./types";

// Lightweight builders. We bypass the editor's defaultPayload helpers so each
// test owns the exact tree shape; uids are stable strings to make ID lookups
// readable in assertions.
let uidSeq = 0;
function uid(): string {
	return `u${++uidSeq}`;
}

function leaf(type: ConditionNode["type"] = "threshold"): ConditionNode {
	return { type, _uid: uid() };
}

function and(...children: ConditionNode[]): ConditionNode {
	return {
		type: "composite",
		_uid: uid(),
		composite: { operator: "and", conditions: children },
	};
}

function or(...children: ConditionNode[]): ConditionNode {
	return {
		type: "composite",
		_uid: uid(),
		composite: { operator: "or", conditions: children },
	};
}

function not(child: ConditionNode): ConditionNode {
	return { type: "not", _uid: uid(), not: { child } };
}

function sustained(minutes: number, child: ConditionNode): ConditionNode {
	return {
		type: "sustained",
		_uid: uid(),
		sustained: { minutes, child },
	};
}

function alertState(
	alertId: string,
	state: "firing" | "unacknowledged" | "acknowledged" = "firing",
	forMinutes?: number,
): ConditionNode {
	return {
		type: "alert_state",
		_uid: uid(),
		alert_state: { alert_id: alertId, state, for_minutes: forMinutes },
	};
}

function makeRule(id: string, condition: ConditionNode): AlertRuleResponse {
	return {
		id,
		name: id,
		conditionType: condition.type as AlertRuleResponse["conditionType"],
		conditionParams: (condition as Record<string, unknown>)[condition.type],
	};
}

function logFrom(
	entries: Array<{ ruleId: string; leafId: number; points: Array<[number, boolean]> }>,
): LeafTransitionLog {
	const byRule: Record<string, ApiLeafTransitionLog[]> = {};
	for (const e of entries) {
		(byRule[e.ruleId] ??= []).push({
			leafId: e.leafId,
			points: e.points.map(([atMs, value]) => ({ atMs, value })),
		});
	}
	return new LeafTransitionLog(byRule);
}

describe("assignLeafIds", () => {
	it("assigns id 0 to a single leaf", () => {
		const root = leaf("threshold");
		const ids = assignLeafIds(root);
		expect(ids.size).toBe(1);
		expect(ids.get(root._uid!)).toBe(0);
	});

	it("assigns sequential ids to AND of two leaves in source order", () => {
		const a = leaf("threshold");
		const b = leaf("trend");
		const root = and(a, b);
		const ids = assignLeafIds(root);
		expect(ids.size).toBe(2);
		expect(ids.get(a._uid!)).toBe(0);
		expect(ids.get(b._uid!)).toBe(1);
	});

	it("assigns ids to inner leaves only when wrapped in NOT", () => {
		const inner = leaf("threshold");
		const wrapped = not(inner);
		const a = leaf("trend");
		const root = and(wrapped, a);
		const ids = assignLeafIds(root);
		expect(ids.size).toBe(2);
		expect(ids.has(wrapped._uid!)).toBe(false);
		expect(ids.get(inner._uid!)).toBe(0);
		expect(ids.get(a._uid!)).toBe(1);
	});

	it("assigns ids to inner leaves only when wrapped in sustained", () => {
		const inner = leaf("threshold");
		const wrapped = sustained(5, inner);
		const ids = assignLeafIds(wrapped);
		expect(ids.size).toBe(1);
		expect(ids.has(wrapped._uid!)).toBe(false);
		expect(ids.get(inner._uid!)).toBe(0);
	});

	it("DFS pre-order: AND(a, OR(b, c)) → a=0, b=1, c=2", () => {
		const a = leaf("threshold");
		const b = leaf("trend");
		const c = leaf("iob");
		const root = and(a, or(b, c));
		const ids = assignLeafIds(root);
		expect(ids.get(a._uid!)).toBe(0);
		expect(ids.get(b._uid!)).toBe(1);
		expect(ids.get(c._uid!)).toBe(2);
	});

	it("returns empty map for a tree with no leaves (empty composite)", () => {
		const root: ConditionNode = {
			type: "composite",
			_uid: uid(),
			composite: { operator: "and", conditions: [] },
		};
		expect(assignLeafIds(root).size).toBe(0);
	});
});

describe("LeafTransitionLog.valueAt", () => {
	it("returns undefined before the first point", () => {
		const log = logFrom([{ ruleId: "r", leafId: 0, points: [[1000, true]] }]);
		expect(log.valueAt("r", 0, 999)).toBeUndefined();
		expect(log.valueAt("r", 0, 1000)).toBe(true);
		expect(log.valueAt("r", 0, 2000)).toBe(true);
	});

	it("walks a multi-point sequence correctly", () => {
		const log = logFrom([
			{
				ruleId: "r",
				leafId: 0,
				points: [
					[0, false],
					[500, true],
					[1000, false],
				],
			},
		]);
		expect(log.valueAt("r", 0, 0)).toBe(false);
		expect(log.valueAt("r", 0, 499)).toBe(false);
		expect(log.valueAt("r", 0, 500)).toBe(true);
		expect(log.valueAt("r", 0, 999)).toBe(true);
		expect(log.valueAt("r", 0, 1000)).toBe(false);
		expect(log.valueAt("r", 0, 2000)).toBe(false);
	});

	it("returns undefined for unknown (ruleId, leafId)", () => {
		const log = logFrom([{ ruleId: "r", leafId: 0, points: [[0, true]] }]);
		expect(log.valueAt("missing", 0, 0)).toBeUndefined();
		expect(log.valueAt("r", 99, 0)).toBeUndefined();
	});

	it("binary-searches a 1000-point sequence correctly", () => {
		const points: Array<[number, boolean]> = [];
		for (let i = 0; i < 1000; i++) {
			points.push([i * 10, i % 2 === 0]);
		}
		const log = logFrom([{ ruleId: "r", leafId: 0, points }]);
		expect(log.valueAt("r", 0, 0)).toBe(true);
		expect(log.valueAt("r", 0, 9)).toBe(true);
		expect(log.valueAt("r", 0, 10)).toBe(false);
		// 4995 falls in idx 499 (atMs=4990); 499 is odd → false
		expect(log.valueAt("r", 0, 4995)).toBe(false);
		// idx 500 -> at 5000, even -> true
		expect(log.valueAt("r", 0, 5000)).toBe(true);
		// idx 999 -> at 9990, odd -> false
		expect(log.valueAt("r", 0, 9990)).toBe(false);
		// past last point still resolves to last point's value
		expect(log.valueAt("r", 0, 99999)).toBe(false);
		expect(log.valueAt("r", 0, -1)).toBeUndefined();
	});

	it("sorts unsorted point input on construction", () => {
		const log = new LeafTransitionLog({
			r: [
				{
					leafId: 0,
					points: [
						{ atMs: 1000, value: false },
						{ atMs: 0, value: true },
						{ atMs: 500, value: false },
					],
				},
			],
		});
		expect(log.valueAt("r", 0, 0)).toBe(true);
		expect(log.valueAt("r", 0, 500)).toBe(false);
		expect(log.valueAt("r", 0, 1000)).toBe(false);
	});
});

describe("composeRuleTruth", () => {
	// Tests construct rules from a single ConditionNode they already have, so we
	// stash the source tree on the rule itself for reconstruction. Production
	// callers pass the already-parsed editor tree directly to composeRuleTruth.
	const sourceTrees = new Map<string, ConditionNode>();

	function rule(id: string, condition: ConditionNode): AlertRuleResponse {
		sourceTrees.set(id, condition);
		return makeRule(id, condition);
	}

	function compose(
		r: AlertRuleResponse,
		log: LeafTransitionLog,
		atMs: number,
		extra?: { rules?: AlertRuleResponse[]; disabled?: ReadonlySet<string> },
	): boolean {
		const rules = [r, ...(extra?.rules ?? [])];
		const ruleById = new Map(rules.map((x) => [x.id!, x]));
		const treeByRule = new Map<string, ConditionNode>();
		const leafIdsByRule = new Map<string, Map<string, number>>();
		for (const x of rules) {
			const tree = sourceTrees.get(x.id!);
			if (!tree) continue;
			treeByRule.set(x.id!, tree);
			leafIdsByRule.set(x.id!, assignLeafIds(tree));
		}
		const tree = treeByRule.get(r.id!)!;
		return composeRuleTruth(r, tree, log, atMs, {
			ruleById,
			treeByRule,
			disabledRuleIds: extra?.disabled ?? new Set(),
			leafIdsByRule,
			memo: new Map(),
		});
	}

	it("AND: both true → true, one false → false", () => {
		const a = leaf("threshold");
		const b = leaf("trend");
		const r = rule("r1", and(a, b));
		const log = logFrom([
			{ ruleId: "r1", leafId: 0, points: [[0, true]] },
			{ ruleId: "r1", leafId: 1, points: [[0, true]] },
		]);
		expect(compose(r, log, 100)).toBe(true);

		const log2 = logFrom([
			{ ruleId: "r1", leafId: 0, points: [[0, true]] },
			{ ruleId: "r1", leafId: 1, points: [[0, false]] },
		]);
		expect(compose(r, log2, 100)).toBe(false);
	});

	it("OR: any true → true, all false → false", () => {
		const a = leaf("threshold");
		const b = leaf("trend");
		const r = rule("r2", or(a, b));
		const log = logFrom([
			{ ruleId: "r2", leafId: 0, points: [[0, false]] },
			{ ruleId: "r2", leafId: 1, points: [[0, true]] },
		]);
		expect(compose(r, log, 100)).toBe(true);

		const log2 = logFrom([
			{ ruleId: "r2", leafId: 0, points: [[0, false]] },
			{ ruleId: "r2", leafId: 1, points: [[0, false]] },
		]);
		expect(compose(r, log2, 100)).toBe(false);
	});

	it("NOT: inverts child", () => {
		const inner = leaf("threshold");
		const r = rule("r3", and(not(inner)));
		const log = logFrom([{ ruleId: "r3", leafId: 0, points: [[0, false]] }]);
		expect(compose(r, log, 100)).toBe(true);

		const log2 = logFrom([{ ruleId: "r3", leafId: 0, points: [[0, true]] }]);
		expect(compose(r, log2, 100)).toBe(false);
	});

	it("sustained: true for 10min satisfies 5min requirement", () => {
		const inner = leaf("threshold");
		const r = rule("r4", and(sustained(5, inner)));
		// leaf became true at t=0, query at t=10min
		const log = logFrom([{ ruleId: "r4", leafId: 0, points: [[0, true]] }]);
		const tenMin = 10 * 60_000;
		expect(compose(r, log, tenMin)).toBe(true);
	});

	it("sustained: true for 3min fails 5min requirement", () => {
		const inner = leaf("threshold");
		const r = rule("r5", and(sustained(5, inner)));
		// leaf flipped true 3 minutes before query
		const queryAt = 100 * 60_000;
		const flipAt = queryAt - 3 * 60_000;
		const log = logFrom([
			{
				ruleId: "r5",
				leafId: 0,
				points: [
					[0, false],
					[flipAt, true],
				],
			},
		]);
		expect(compose(r, log, queryAt)).toBe(false);
	});

	it("alert_state: cross-rule reference composes referenced rule's truth", () => {
		const xLeaf = leaf("threshold");
		const x = rule("X", and(xLeaf));
		const y = rule("Y", and(alertState("X")));
		const log = logFrom([{ ruleId: "X", leafId: 0, points: [[0, true]] }]);
		expect(compose(y, log, 100, { rules: [x] })).toBe(true);

		const log2 = logFrom([{ ruleId: "X", leafId: 0, points: [[0, false]] }]);
		expect(compose(y, log2, 100, { rules: [x] })).toBe(false);
	});

	it("alert_state: disabled referenced rule resolves to false", () => {
		const xLeaf = leaf("threshold");
		const x = rule("Xd", and(xLeaf));
		const y = rule("Yd", and(alertState("Xd")));
		const log = logFrom([{ ruleId: "Xd", leafId: 0, points: [[0, true]] }]);
		expect(
			compose(y, log, 100, { rules: [x], disabled: new Set(["Xd"]) }),
		).toBe(false);
	});

	it("alert_state: missing referenced rule resolves to false", () => {
		const y = rule("Ym", and(alertState("does-not-exist")));
		const log = logFrom([]);
		expect(compose(y, log, 100)).toBe(false);
	});

	it("alert_state: cycle detection returns false", () => {
		const x = rule("Xc", and(alertState("Yc")));
		const y = rule("Yc", and(alertState("Xc")));
		const log = logFrom([]);
		expect(compose(x, log, 100, { rules: [y] })).toBe(false);
		expect(compose(y, log, 100, { rules: [x] })).toBe(false);
	});

	it("alert_state with for_minutes requires sustained truth at boundary", () => {
		const xLeaf = leaf("threshold");
		const x = rule("Xf", and(xLeaf));
		const y = rule("Yf", and(alertState("Xf", "firing", 5)));
		// X has been firing for 10 min — both endpoints true
		const queryAt = 20 * 60_000;
		const log = logFrom([{ ruleId: "Xf", leafId: 0, points: [[0, true]] }]);
		expect(compose(y, log, queryAt, { rules: [x] })).toBe(true);

		// X only became true 2 min ago; for_minutes=5 → boundary at queryAt - 5min has X=false
		const recentLog = logFrom([
			{
				ruleId: "Xf",
				leafId: 0,
				points: [
					[0, false],
					[queryAt - 2 * 60_000, true],
				],
			},
		]);
		expect(compose(y, recentLog, queryAt, { rules: [x] })).toBe(false);
	});

	it("mixed tree: AND(threshold, NOT(trend), sustained(5min, iob))", () => {
		const t = leaf("threshold");
		const tr = leaf("trend");
		const i = leaf("iob");
		const r = rule("rmix", and(t, not(tr), sustained(5, i)));
		// id 0 = threshold, id 1 = trend (inside NOT), id 2 = iob (inside sustained)
		const queryAt = 30 * 60_000;
		const log = logFrom([
			{ ruleId: "rmix", leafId: 0, points: [[0, true]] },
			{ ruleId: "rmix", leafId: 1, points: [[0, false]] }, // NOT inverts → true
			{ ruleId: "rmix", leafId: 2, points: [[0, true]] }, // sustained 30min
		]);
		expect(compose(r, log, queryAt)).toBe(true);
	});

	it("leaf with no transition data resolves to false", () => {
		const a = leaf("threshold");
		const r = rule("rno", and(a));
		const log = logFrom([]);
		expect(compose(r, log, 100)).toBe(false);
	});

	it("works on a JSON-deserialised rule (no pre-existing uids on payload)", () => {
		// Simulates a rule freshly fetched from the API — conditionParams comes
		// straight from JSON, so neither it nor any nested children have _uid.
		const r: AlertRuleResponse = {
			id: "rJson",
			name: "rJson",
			conditionType: "composite" as AlertRuleResponse["conditionType"],
			conditionParams: {
				operator: "and",
				conditions: [
					{ type: "threshold", threshold: { direction: "below", value: 70 } },
					{ type: "trend", trend: { bucket: "falling" } },
				],
			},
		};
		const log = logFrom([
			{ ruleId: "rJson", leafId: 0, points: [[0, true]] },
			{ ruleId: "rJson", leafId: 1, points: [[0, true]] },
		]);
		const tree = nodeFromApi(r.conditionType, r.conditionParams)!;
		const leafIdsByRule = new Map([[r.id!, assignLeafIds(tree)]]);
		const treeByRule = new Map([[r.id!, tree]]);
		const result = composeRuleTruth(r, tree, log, 100, {
			ruleById: new Map([[r.id!, r]]),
			treeByRule,
			disabledRuleIds: new Set(),
			leafIdsByRule,
			memo: new Map(),
		});
		expect(result).toBe(true);
	});

	it("sustained-of-composite: AND of two leaves true at boundary and at atMs", () => {
		const a = leaf("threshold");
		const b = leaf("trend");
		const r = rule("rsoc", and(sustained(5, and(a, b))));
		const queryAt = 30 * 60_000;
		// Both leaves are true from t=0 onwards — true at boundary (queryAt - 5min) and at queryAt.
		const log = logFrom([
			{ ruleId: "rsoc", leafId: 0, points: [[0, true]] },
			{ ruleId: "rsoc", leafId: 1, points: [[0, true]] },
		]);
		expect(compose(r, log, queryAt)).toBe(true);
	});

	it("alert_state acknowledged/unacknowledged falls back to firing semantics", () => {
		const xLeaf = leaf("threshold");
		const x = rule("Xa", and(xLeaf));
		const y = rule("Ya", and(alertState("Xa", "acknowledged")));
		const log = logFrom([{ ruleId: "Xa", leafId: 0, points: [[0, true]] }]);
		expect(compose(y, log, 100, { rules: [x] })).toBe(true);
	});
});
