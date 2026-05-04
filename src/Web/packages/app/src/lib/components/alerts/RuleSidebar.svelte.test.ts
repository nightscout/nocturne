import { render } from "vitest-browser-svelte";
import { describe, it, expect, beforeEach } from "vitest";
import RuleSidebar from "./RuleSidebar.svelte";

beforeEach(() => {
	try {
		sessionStorage.clear();
	} catch {
		// ignore
	}
});
import { LeafTransitionLog, assignLeafIds } from "./leafEval";
import type { AlertRuleResponse } from "$api-clients";
import type { ConditionNode } from "./types";

function leaf(uid: string): ConditionNode {
	return { type: "threshold", _uid: uid, threshold: { direction: "below", value: 70 } };
}

function and(uid: string, ...children: ConditionNode[]): ConditionNode {
	return {
		type: "composite",
		_uid: uid,
		composite: { operator: "and", conditions: children },
	};
}

function makeRule(id: string, name: string, condition: ConditionNode): AlertRuleResponse {
	return {
		id,
		name,
		severity: "warning",
		isEnabled: true,
		sortOrder: 0,
		conditionType: "composite",
		conditionParams: undefined,
		channels: [],
	} as unknown as AlertRuleResponse;
}

describe("RuleSidebar", () => {
	it("renders a row per rule and shows leaf pips colored by transition state", async () => {
		const tree = and("g", leaf("a"), leaf("b"));
		const rule = makeRule("R1", "Rule One", tree);
		const ids = assignLeafIds(tree); // a→0, b→1
		const T = 1_000_000;
		const log = new LeafTransitionLog({
			R1: [
				{ leafId: 0, points: [{ atMs: 0, value: false }, { atMs: 500, value: true }] },
				{ leafId: 1, points: [{ atMs: 0, value: false }] },
			],
		});

		render(RuleSidebar, {
			rules: [rule],
			editingRuleId: "R1",
			treeByRule: new Map([["R1", tree]]),
			leafIdsByRule: new Map([["R1", ids]]),
			leafLog: log,
			currentTimeMs: T,
			disabledRuleIds: new Set<string>(),
			availableRules: [{ id: "R1", name: "Rule One" }],
		});

		const leaves = document.querySelectorAll('[data-testid="rule-leaf"]');
		expect(leaves.length).toBe(2);
		// Editing rule expands by default, so both leaves are in the DOM.
		const byId: Record<string, Element> = {};
		for (const el of Array.from(leaves)) {
			byId[el.getAttribute("data-leaf-id") ?? ""] = el;
		}
		expect(byId["0"]?.getAttribute("data-truth")).toBe("true");
		expect(byId["1"]?.getAttribute("data-truth")).toBe("false");
	});

	it("rule status pip reflects composed truth (false when a leaf is false in AND)", async () => {
		const tree = and("g", leaf("a"), leaf("b"));
		const rule = makeRule("R1", "Rule One", tree);
		const ids = assignLeafIds(tree);
		const log = new LeafTransitionLog({
			R1: [
				{ leafId: 0, points: [{ atMs: 0, value: true }] },
				{ leafId: 1, points: [{ atMs: 0, value: false }] },
			],
		});
		render(RuleSidebar, {
			rules: [rule],
			editingRuleId: "R1",
			treeByRule: new Map([["R1", tree]]),
			leafIdsByRule: new Map([["R1", ids]]),
			leafLog: log,
			currentTimeMs: 100,
			disabledRuleIds: new Set<string>(),
			availableRules: [],
		});
		const pip = document.querySelector('[data-testid="rule-status-pip"]');
		expect(pip?.getAttribute("data-truth")).toBe("false");
	});

	it("rule status pip is true when both leaves of an AND are true", async () => {
		const tree = and("g", leaf("a"), leaf("b"));
		const rule = makeRule("R1", "Rule One", tree);
		const ids = assignLeafIds(tree);
		const log = new LeafTransitionLog({
			R1: [
				{ leafId: 0, points: [{ atMs: 0, value: true }] },
				{ leafId: 1, points: [{ atMs: 0, value: true }] },
			],
		});
		render(RuleSidebar, {
			rules: [rule],
			editingRuleId: "R1",
			treeByRule: new Map([["R1", tree]]),
			leafIdsByRule: new Map([["R1", ids]]),
			leafLog: log,
			currentTimeMs: 100,
			disabledRuleIds: new Set<string>(),
			availableRules: [],
		});
		const pip = document.querySelector('[data-testid="rule-status-pip"]');
		expect(pip?.getAttribute("data-truth")).toBe("true");
	});

	it("disabled rules compose to false even when leaves are true", async () => {
		const tree = and("g", leaf("a"));
		const rule = makeRule("R1", "Rule One", tree);
		const ids = assignLeafIds(tree);
		const log = new LeafTransitionLog({
			R1: [{ leafId: 0, points: [{ atMs: 0, value: true }] }],
		});
		render(RuleSidebar, {
			rules: [rule],
			editingRuleId: "R1",
			treeByRule: new Map([["R1", tree]]),
			leafIdsByRule: new Map([["R1", ids]]),
			leafLog: log,
			currentTimeMs: 100,
			disabledRuleIds: new Set<string>(["R1"]),
			availableRules: [],
		});
		const pip = document.querySelector('[data-testid="rule-status-pip"]');
		expect(pip?.getAttribute("data-truth")).toBe("false");
	});
});
