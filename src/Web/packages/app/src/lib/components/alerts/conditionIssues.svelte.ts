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
	"invalid_field:timezone": "A time-of-day condition has a time zone that isn't recognised. Pick one from its time zone list, or clear it to use the profile's, and save again.",
	minutes_not_positive: "A “for at least” duration must be 1 minute or more.",
	"minutes_not_positive:timeout_minutes": "A signal loss condition needs a time without readings of 1 minute or more.",
	"minutes_not_positive:within_minutes": "A predicted glucose condition needs to look ahead 1 minute or more.",
	minutes_negative: "A condition compares a time since something against a negative number of minutes, so it would be the same every time it is checked. Use 0 or more.",
	child_missing: "A NOT or “for at least” wrapper has nothing inside it.",
	field_missing: "A condition is missing a setting it needs. Fill it in and save again.",
	"field_missing:alert_id": "An alert-state condition needs a rule chosen.",
	"field_missing:tracker_definition_id": "A tracker condition needs a tracker chosen.",
	"field_missing:from": "A time-of-day condition needs both a start and an end time.",
	"field_missing:to": "A time-of-day condition needs both a start and an end time.",
	unknown_value: "A condition has a choice that isn't recognised. Pick one from its list and save again.",
	invalid_time: "A time-of-day condition needs its times as hours and minutes, like 09:00.",
	empty_window: "A time-of-day condition starts and ends at the same time, so it would never be true. Choose different times.",
	"list_empty:buckets": "A glucose bucket condition needs at least one bucket chosen.",
	"list_empty:days": "A day-of-week condition needs at least one day chosen.",
	list_empty: "A condition needs at least one option chosen.",
	pump_mode_category: "Pump modes can't be checked with a “State active” condition. Use a “Pump mode” condition instead.",
	unknown_field: "Part of this rule's conditions has a setting its kind of condition doesn't have, so it can't be saved.",
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
