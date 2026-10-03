import type { CompareOperator, ConditionNode, GroupNode, LeafKind, LeafNode } from "./wire";

function isLeaf(node: ConditionNode): node is LeafNode {
	return node.type !== "composite" && node.type !== "not" && node.type !== "sustained";
}

export const OPERATORS: { operator: CompareOperator; label: string }[] = [
	{ operator: ">=", label: "at least" },
	{ operator: ">", label: "more than" },
	{ operator: "<", label: "less than" },
	{ operator: "<=", label: "at most" },
];

export const LEAF_KINDS: { kind: LeafKind; label: string }[] = [
	{ kind: "threshold", label: "Glucose" },
	{ kind: "rate_of_change", label: "Rate of change" },
	{ kind: "signal_loss", label: "Signal loss" },
	{ kind: "time_of_day", label: "Time of day" },
	{ kind: "cob", label: "Carbs on board" },
	{ kind: "iob", label: "Insulin on board" },
	{ kind: "time_since_last_carb", label: "Time since carbs" },
];

export function defaultLeaf(kind: LeafKind): LeafNode {
	switch (kind) {
		case "threshold":
			return { type: "threshold", threshold: { direction: "below", value: 70 } };
		case "rate_of_change":
			return { type: "rate_of_change", rate_of_change: { direction: "falling", rate: 3 } };
		case "signal_loss":
			return { type: "signal_loss", signal_loss: { timeout_minutes: 30 } };
		case "time_of_day":
			return { type: "time_of_day", time_of_day: { from: "22:00", to: "06:00" } };
		case "cob":
			return { type: "cob", cob: { operator: ">=", value: 5 } };
		case "iob":
			return { type: "iob", iob: { operator: "<", value: 0.5 } };
		case "time_since_last_carb":
			return { type: "time_since_last_carb", time_since_last_carb: { operator: "<", minutes: 30 } };
	}
}

/** A row as the app's editor draws it: a condition, optionally NOT, optionally held for minutes. */
export interface Row {
	negated: boolean;
	minutes: number | null;
	leaf: LeafNode;
}

/** Null for a nested group, which is its own editor. Wrappers nest as not(sustained(leaf)). */
export function toRow(node: ConditionNode): Row | null {
	let negated = false;
	let minutes: number | null = null;
	let inner = node;
	if (inner.type === "not") {
		negated = true;
		inner = inner.not.child;
	}
	if (inner.type === "sustained") {
		minutes = inner.sustained.minutes;
		inner = inner.sustained.child;
	}
	return isLeaf(inner) ? { negated, minutes, leaf: inner } : null;
}

export function fromRow({ negated, minutes, leaf }: Row): ConditionNode {
	const held: ConditionNode = minutes === null ? leaf : { type: "sustained", sustained: { minutes, child: leaf } };
	return negated ? { type: "not", not: { child: held } } : held;
}

/** The editor always edits a group, as the app's does. */
export function asGroup(node: ConditionNode): GroupNode {
	return node.type === "composite"
		? node
		: { type: "composite", composite: { operator: "and", conditions: [node] } };
}

/** A one-condition group is saved as that condition, as the app saves it. */
export function collapse(node: ConditionNode): ConditionNode {
	return node.type === "composite" && node.composite.conditions.length === 1
		? node.composite.conditions[0]
		: node;
}
