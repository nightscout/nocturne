export type CompareOperator = ">" | ">=" | "<" | "<=" | "==";

/**
 * A condition node in the engine's wire shape (docs/alerts/engine-semantics.md §1.2): the payload
 * sits under a property named after its `type`.
 */
export type ConditionNode =
	| { type: "composite"; composite: { operator: "and" | "or"; conditions: ConditionNode[] } }
	| { type: "not"; not: { child: ConditionNode } }
	| { type: "sustained"; sustained: { minutes: number; child: ConditionNode } }
	| { type: "threshold"; threshold: { direction: "above" | "below"; value: number } }
	| { type: "rate_of_change"; rate_of_change: { direction: "rising" | "falling"; rate: number } }
	| { type: "signal_loss"; signal_loss: { timeout_minutes: number } }
	| { type: "time_of_day"; time_of_day: { from: string; to: string } }
	| { type: "cob"; cob: { operator: CompareOperator; value: number } }
	| { type: "iob"; iob: { operator: CompareOperator; value: number } }
	| { type: "time_since_last_carb"; time_since_last_carb: { operator: CompareOperator; minutes: number } };

export type GroupNode = Extract<ConditionNode, { type: "composite" }>;
export type LeafNode = Exclude<ConditionNode, { type: "composite" | "not" | "sustained" }>;
export type LeafKind = LeafNode["type"];

export type Severity = "critical" | "warning" | "info";

export interface SimRule {
	id: string;
	name: string;
	severity: Severity;
	condition: ConditionNode;
	autoResolve?: ConditionNode;
}

/** The replay context fields the demo's conditions read; everything else is left absent. */
export interface TickContext {
	latest_value?: number;
	latest_timestamp?: string;
	trend_rate?: number;
	last_reading_at?: string;
	iob_units?: number;
	cob_grams?: number;
	last_carb_at?: string;
	last_bolus_at?: string;
	tenant_time_zone_id: string;
}

export interface ReplayTick {
	at: string;
	context: TickContext;
}

export type ReplayEventKind = "fired" | "suppressed_by_dnd" | "auto_resolved" | "cleared";

export interface ReplayResult {
	order: string[];
	events: { at: string; rule_id: string; kind: ReplayEventKind }[];
	leaf_transitions: {
		rule_id: string;
		leaves: { leaf_id: number; points: { at_ms: number; value: boolean }[] }[];
	}[];
	ticks: {
		at: string;
		rules: ({ rule_id: string; met: boolean; firing: boolean } | { rule_id: string; skipped: true })[];
	}[];
}

export interface ValidationIssue {
	scope: string;
	path: string;
	reason: string;
	field?: string | null;
}

/** The payload a node carries under the property named after its `type`. */
export function wirePayload(node: ConditionNode): unknown {
	switch (node.type) {
		case "composite":
			return node.composite;
		case "not":
			return node.not;
		case "sustained":
			return node.sustained;
		case "threshold":
			return node.threshold;
		case "rate_of_change":
			return node.rate_of_change;
		case "signal_loss":
			return node.signal_loss;
		case "time_of_day":
			return node.time_of_day;
		case "cob":
			return node.cob;
		case "iob":
			return node.iob;
		case "time_since_last_carb":
			return node.time_since_last_carb;
	}
}
