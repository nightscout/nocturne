import { suggestAutoResolve } from "#lib/api/generated/alertRules.generated.remote.js";
import {
	ensureCompositeRoot,
	nodeFromApiEnvelope,
	stripEditorFields,
	type ConditionNode,
} from "./types.js";

/**
 * The condition that closes an alert at once: the opposite of `firing`, as the API works it out.
 * `null` when the conditions have no opposite. It is a group, as the rule builder edits one.
 */
export async function oppositeCondition(firing: ConditionNode | null): Promise<ConditionNode | null> {
	if (!firing) return null;
	const { condition } = await suggestAutoResolve({ condition: stripEditorFields(firing) });
	const node = nodeFromApiEnvelope(condition);
	return node ? ensureCompositeRoot(node) : null;
}
