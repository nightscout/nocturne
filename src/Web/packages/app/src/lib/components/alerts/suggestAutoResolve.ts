import {
	stripEditorFields,
	type ConditionNode,
	type ComparisonOperator,
} from "./types";

/**
 * Derive an auto-resolve condition tree from a firing tree by inverting each
 * leaf's predicate. The output is *always* a composite (so the editor can
 * keep editing it in place) — single-leaf inputs come back as a one-child
 * AND group, mirroring the inline rule builder's "always edit at the group
 * level" invariant.
 *
 * The intent is "the alert is no longer triggering" — i.e. the *negation* of
 * the firing condition. For an AND group of leaves we flip each leaf and
 * combine with OR (the alert resolves when *any* one stops being true). For
 * an OR group we flip each leaf and combine with AND (the alert resolves
 * only when *every* path that could keep it firing has stopped). Wrappers
 * (NOT, sustained) and unsupported leaf shapes are skipped — the editor
 * surfaces the suggestion as a starting point, not a contractual inverse.
 */
export function suggestAutoResolve(firing: ConditionNode | null): ConditionNode | null {
	if (!firing) return null;
	const inverted = invertNode(firing);
	if (!inverted) return null;
	if (inverted.type === "composite") return inverted;
	return {
		type: "composite",
		_uid: undefined,
		composite: { operator: "and", conditions: [inverted] },
	};
}

function invertNode(node: ConditionNode): ConditionNode | null {
	switch (node.type) {
		case "composite": {
			const p = node.composite;
			if (!p || p.conditions.length === 0) return null;
			const inverted = p.conditions
				.map((c) => invertNode(c))
				.filter((c): c is ConditionNode => c !== null);
			if (inverted.length === 0) return null;
			// AND of firing-leaves becomes OR of inverted-leaves (any one stops →
			// alert resolves); OR becomes AND (every path must stop).
			const flipped: "and" | "or" = p.operator === "and" ? "or" : "and";
			if (inverted.length === 1) return inverted[0];
			return {
				type: "composite",
				composite: { operator: flipped, conditions: inverted },
			};
		}
		case "not":
			// Already negated — drop the wrapper rather than invert again.
			// stripEditorFields recurses to clear subtree _uid leaks (the editor
			// re-stamps fresh ids on the next assignUidsRecursive pass).
			return node.not?.child ? stripEditorFields(node.not.child) : null;
		case "sustained":
			// Strip the sustained wrapper for the resolve heuristic — auto-resolve
			// fires the moment the condition lifts, not after another sustain.
			return node.sustained?.child ? invertNode(node.sustained.child) : null;
		case "threshold": {
			const p = node.threshold;
			if (!p) return null;
			return {
				type: "threshold",
				threshold: {
					direction: p.direction === "below" ? "above" : "below",
					value: p.value,
				},
			};
		}
		case "rate_of_change": {
			const p = node.rate_of_change;
			if (!p) return null;
			// The inverse of "rising at >= R" is "rising at < R" — i.e. trending in
			// the same direction but slower than the trigger. We approximate via a
			// trend bucket (flat) since rate_of_change has no operator field.
			return { type: "trend", trend: { bucket: "flat" } };
		}
		case "predicted": {
			const p = node.predicted;
			if (!p) return null;
			return {
				type: "predicted",
				predicted: {
					operator: invertOperator(p.operator as ComparisonOperator),
					value: p.value,
					within_minutes: p.within_minutes,
				},
			};
		}
		case "trend":
			return { type: "trend", trend: { bucket: "flat" } };
		case "staleness": {
			const p = node.staleness;
			if (!p) return null;
			return {
				type: "staleness",
				staleness: {
					operator: invertOperator(p.operator as ComparisonOperator),
					value: p.value,
				},
			};
		}
		case "iob":
		case "cob":
		case "reservoir":
		case "site_age":
		case "sensor_age":
		case "pump_battery":
		case "uploader_battery":
		case "sensitivity_ratio": {
			const payload = node[node.type];
			if (!payload) return null;
			return {
				type: node.type,
				[node.type]: {
					operator: invertOperator(payload.operator as ComparisonOperator),
					value: payload.value,
				},
			} as ConditionNode;
		}
		case "loop_stale":
		case "loop_enaction_stale":
			// Loop liveness only supports `>` / `>=` operators — there's no
			// clean "freshness" inverse, and cross-typing into BG staleness
			// would conflate CGM freshness with loop liveness. Skip and let
			// the user fill in a domain-appropriate resolve.
			return null;
		case "pump_suspended":
		case "override_active":
		case "do_not_disturb": {
			const payload = node[node.type];
			if (!payload) return null;
			return {
				type: node.type,
				[node.type]: { is_active: !payload.is_active },
			} as ConditionNode;
		}
		case "alert_state": {
			const p = node.alert_state;
			if (!p) return null;
			// "Other rule firing" → "other rule acknowledged".
			return {
				type: "alert_state",
				alert_state: {
					alert_id: p.alert_id,
					state: p.state === "firing" ? "acknowledged" : "firing",
					for_minutes: p.for_minutes,
				},
			};
		}
		case "time_of_day":
			// Time windows don't invert cleanly without a NOT wrapper; suggesting
			// the same window doesn't help. Skip.
			return null;
		case "temp_basal":
		case "signal_loss":
			// Domain inversion isn't unambiguous; let the user fill these in.
			return null;
	}
}

function invertOperator(op: ComparisonOperator): ComparisonOperator {
	switch (op) {
		case "<":
			return ">=";
		case "<=":
			return ">";
		case ">":
			return "<=";
		case ">=":
			return "<";
	}
}

