const REVIEW_MESSAGES: Record<string, string> = {
	legacy_condition_converted:
		"This rule was set up before alerts were redesigned and has been converted. Its confirmation readings and hysteresis could not be carried over, so it alerts on the first reading that meets its condition. Check its conditions and save it.",
	legacy_threshold_implausible:
		"This rule was set up before alerts were redesigned and has been converted, but its glucose threshold is missing, not a number, or below 40 mg/dL, which may be a mmol/L value. Its confirmation readings and hysteresis could not be carried over either. Check its threshold and save it.",
	condition_type_unrecognized:
		"This rule's condition type isn't one Nocturne knows, so it was turned off. Edit its conditions and save it.",
	severity_unrecognized:
		"This rule's severity wasn't one Nocturne knows, so it was set to critical. Check its severity and save it.",
};

const FALLBACK = "This rule was changed by an update. Check it and save it.";

/**
 * What to tell the person about a rule an update changed or turned off
 * (the rule's `reviewReason`), or `null` when none did.
 */
export function reviewReasonMessage(reason: string | null | undefined): string | null {
	if (!reason) return null;
	return Object.hasOwn(REVIEW_MESSAGES, reason) ? REVIEW_MESSAGES[reason] : FALLBACK;
}
