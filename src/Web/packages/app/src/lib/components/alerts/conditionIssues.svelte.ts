import { errorMessage, errorStatus } from "$lib/forms/submit-error";

const MALFORMED = "Part of this rule's conditions is malformed, so it can't be saved.";

/**
 * Sentences for the reason codes a rule save is rejected with
 * (docs/alerts/engine-semantics.md §1.4). A code arrives alone or as
 * `reason:field`; the specific form wins.
 */
const ISSUE_MESSAGES: Record<string, string> = {
	conditions_missing: "Every group needs at least one condition.",
	conditions_empty: "Every group needs at least one condition.",
	operator_missing: "A condition has no comparison chosen. Pick one and save again.",
	unknown_operator: "A condition has a comparison that isn't recognised. Pick one from the list and save again.",
	direction_missing: "A glucose condition needs a direction: above or below, rising or falling.",
	unknown_direction: "A glucose condition needs a direction: above or below, rising or falling.",
	state_missing: "An alert-state condition needs a state: firing, unacknowledged or acknowledged.",
	unknown_state: "An alert-state condition needs a state: firing, unacknowledged or acknowledged.",
	"invalid_field:alert_id": "An alert-state condition needs a rule chosen.",
	"invalid_field:tracker_definition_id": "A tracker condition needs a tracker chosen.",
	minutes_not_positive: "A “for at least” duration must be 1 minute or more.",
	child_missing: "A NOT or “for at least” wrapper has nothing inside it.",
	condition_missing: MALFORMED,
	type_missing: MALFORMED,
	unknown_kind: MALFORMED,
	non_canonical_type: MALFORMED,
	payload_missing: MALFORMED,
	not_an_object: MALFORMED,
	invalid_field: MALFORMED,
	too_deep: MALFORMED,
};

/**
 * What to tell the person when a rule save was rejected for its conditions, or
 * `null` when the rejection was for something else. The generated command
 * forwards the rejection's codes as its message, joined by `, ` and `; `.
 */
export function conditionIssuesMessage(err: unknown): string | null {
	if (errorStatus(err) !== 400) return null;
	const message = errorMessage(err);
	if (message === undefined) return null;

	const sentences: string[] = [];
	for (const code of message.split(/[;,]\s*/)) {
		const sentence = ISSUE_MESSAGES[code] ?? ISSUE_MESSAGES[code.split(":")[0]];
		if (sentence === undefined) return null;
		if (!sentences.includes(sentence)) sentences.push(sentence);
	}
	return sentences.join(" ");
}
