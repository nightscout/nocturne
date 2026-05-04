import { defaultPayload, type ConditionNode } from "./types";
import type { LeafKind } from "./factCatalog";

// All mutations operate in place on the Svelte 5 deep-proxy state passed in by
// the editor — RuleBuilder binds `node` and the proxy propagates child writes
// back to the parent. The functions here factor the mutation logic out of the
// component so they can be unit-tested against plain objects.

export function addLeaf(parent: ConditionNode, kind: LeafKind): void {
	if (!parent.composite) return;
	parent.composite.conditions.push(defaultPayload(kind));
}

export function addGroup(parent: ConditionNode, operator: "and" | "or"): void {
	if (!parent.composite) return;
	const seed = defaultPayload("composite");
	if (seed.composite) seed.composite.operator = operator;
	parent.composite.conditions.push(seed);
}

export function removeChild(parent: ConditionNode, index: number): void {
	if (!parent.composite) return;
	parent.composite.conditions.splice(index, 1);
}

export type WrapKind = "and" | "or" | "not" | "sustained";

/**
 * Wrap the child at `index` in a wrapper node, preserving the original as the
 * wrapper's only child. The original `_uid` stays on the inner node so the
 * keyed each block doesn't collapse.
 */
export function wrapChild(
	parent: ConditionNode,
	index: number,
	wrapper: WrapKind,
): void {
	if (!parent.composite) return;
	const inner = parent.composite.conditions[index];
	if (!inner) return;
	let next: ConditionNode;
	if (wrapper === "not") {
		next = { ...defaultPayload("not"), not: { child: inner } };
	} else if (wrapper === "sustained") {
		next = {
			...defaultPayload("sustained"),
			sustained: { minutes: 15, child: inner },
		};
	} else {
		next = {
			...defaultPayload("composite"),
			composite: { operator: wrapper, conditions: [inner] },
		};
	}
	parent.composite.conditions[index] = next;
}

/**
 * Inverse of {@link wrapChild}. If the child is a NOT, sustained, or
 * single-child composite, replace it with its inner node. Multi-child
 * composites stay put — flattening would lose the sibling conditions.
 */
export function unwrapChild(parent: ConditionNode, index: number): void {
	if (!parent.composite) return;
	const c = parent.composite.conditions[index];
	if (!c) return;
	if (c.type === "not" && c.not?.child) {
		parent.composite.conditions[index] = c.not.child;
	} else if (c.type === "sustained" && c.sustained?.child) {
		parent.composite.conditions[index] = c.sustained.child;
	} else if (
		c.type === "composite" &&
		c.composite &&
		c.composite.conditions.length === 1
	) {
		parent.composite.conditions[index] = c.composite.conditions[0];
	}
}

/**
 * Walk past NOT and sustained wrappers to reach the underlying leaf. Returns
 * the leaf's kind, or `null` for a composite (groups don't have a leaf
 * descriptor — they render their own group header).
 */
export function rowLeafKind(c: ConditionNode): LeafKind | null {
	const inner = rowLeafNode(c);
	if (inner.type === "composite") return null;
	return inner.type as LeafKind;
}

/** Walk past NOT and sustained wrappers to reach the underlying leaf node. */
export function rowLeafNode(c: ConditionNode): ConditionNode {
	let cur: ConditionNode = c;
	while (cur.type === "not" && cur.not) cur = cur.not.child;
	while (cur.type === "sustained" && cur.sustained) cur = cur.sustained.child;
	return cur;
}

/** Eyebrow label for the row at `index` in a composite of `op`. */
export function eyebrow(index: number, op: "and" | "or"): string {
	if (index === 0) return "IF";
	return op === "and" ? "AND" : "OR";
}
