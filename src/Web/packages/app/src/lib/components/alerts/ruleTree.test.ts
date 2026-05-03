import { describe, expect, it } from "vitest";
import {
	addLeaf,
	addGroup,
	removeChild,
	wrapChild,
	unwrapChild,
	rowLeafKind,
	rowLeafNode,
	eyebrow,
} from "./ruleTree";
import { defaultPayload, ensureCompositeRoot, type ConditionNode } from "./types";

function newRoot(): ConditionNode {
	return ensureCompositeRoot(defaultPayload("threshold"));
}

describe("addLeaf", () => {
	it("appends a leaf of the requested kind", () => {
		const root = newRoot();
		const initial = root.composite!.conditions.length;
		addLeaf(root, "trend");
		expect(root.composite!.conditions).toHaveLength(initial + 1);
		expect(root.composite!.conditions.at(-1)?.type).toBe("trend");
	});

	it("is a no-op on a non-composite parent", () => {
		const leaf = defaultPayload("threshold");
		expect(() => addLeaf(leaf, "trend")).not.toThrow();
	});
});

describe("addGroup", () => {
	it("appends a composite child with the requested operator", () => {
		const root = newRoot();
		addGroup(root, "or");
		const last = root.composite!.conditions.at(-1);
		expect(last?.type).toBe("composite");
		expect(last?.composite?.operator).toBe("or");
	});
});

describe("removeChild", () => {
	it("splices out the child at the index", () => {
		const root = newRoot();
		addLeaf(root, "trend");
		addLeaf(root, "iob");
		const before = root.composite!.conditions.length;
		removeChild(root, 1);
		expect(root.composite!.conditions).toHaveLength(before - 1);
	});
});

describe("wrapChild / unwrapChild", () => {
	it("wraps in NOT and unwraps back to the original", () => {
		const root = newRoot();
		const inner = root.composite!.conditions[0];
		wrapChild(root, 0, "not");
		expect(root.composite!.conditions[0].type).toBe("not");
		expect(root.composite!.conditions[0].not?.child).toBe(inner);
		unwrapChild(root, 0);
		expect(root.composite!.conditions[0]).toBe(inner);
	});

	it("wraps in sustained with default 15 minutes", () => {
		const root = newRoot();
		wrapChild(root, 0, "sustained");
		expect(root.composite!.conditions[0].type).toBe("sustained");
		expect(root.composite!.conditions[0].sustained?.minutes).toBe(15);
	});

	it("wraps in AND group; unwraps single-child composite", () => {
		const root = newRoot();
		wrapChild(root, 0, "and");
		expect(root.composite!.conditions[0].type).toBe("composite");
		expect(root.composite!.conditions[0].composite?.operator).toBe("and");
		unwrapChild(root, 0);
		expect(root.composite!.conditions[0].type).not.toBe("composite");
	});

	it("does not unwrap multi-child composites", () => {
		const root = newRoot();
		wrapChild(root, 0, "and");
		const wrapped = root.composite!.conditions[0];
		wrapped.composite!.conditions.push(defaultPayload("trend"));
		unwrapChild(root, 0);
		expect(root.composite!.conditions[0]).toBe(wrapped);
	});
});

describe("rowLeafKind / rowLeafNode", () => {
	it("returns the leaf kind for a plain leaf", () => {
		const leaf = defaultPayload("threshold");
		expect(rowLeafKind(leaf)).toBe("threshold");
		expect(rowLeafNode(leaf)).toBe(leaf);
	});

	it("walks past NOT and sustained wrappers", () => {
		const inner = defaultPayload("trend");
		const wrapped: ConditionNode = {
			type: "not",
			not: {
				child: { type: "sustained", sustained: { minutes: 5, child: inner } },
			},
		};
		expect(rowLeafKind(wrapped)).toBe("trend");
		expect(rowLeafNode(wrapped)).toBe(inner);
	});

	it("returns null for a composite", () => {
		const root = newRoot();
		expect(rowLeafKind(root)).toBeNull();
	});
});

describe("eyebrow", () => {
	it("is IF for the first row regardless of operator", () => {
		expect(eyebrow(0, "and")).toBe("IF");
		expect(eyebrow(0, "or")).toBe("IF");
	});

	it("reflects the operator for subsequent rows", () => {
		expect(eyebrow(1, "and")).toBe("AND");
		expect(eyebrow(2, "or")).toBe("OR");
	});
});
