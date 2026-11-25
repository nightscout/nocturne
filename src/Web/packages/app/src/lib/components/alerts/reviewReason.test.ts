import { describe, it, expect } from "vitest";
import { reviewReasonMessage } from "./reviewReason";

describe("reviewReasonMessage", () => {
	it("says nothing about a rule no update changed", () => {
		expect(reviewReasonMessage(undefined)).toBeNull();
		expect(reviewReasonMessage(null)).toBeNull();
	});

	it("explains a converted pre-redesign rule", () => {
		expect(reviewReasonMessage("legacy_condition_converted")).toContain(
			"confirmation readings and hysteresis could not be carried over"
		);
	});

	it("asks for a converted threshold below 40 mg/dL to be checked", () => {
		expect(reviewReasonMessage("legacy_threshold_implausible")).toContain("below 40 mg/dL");
	});

	it("says a rule with an unknown condition type does not run, whether an update turned it off or not", () => {
		expect(reviewReasonMessage("condition_type_unrecognized")).toBe(
			"Nocturne does not know the condition type of this rule, so the rule does not run. Edit its conditions and save it."
		);
	});

	it("says a rule with an unknown severity shows it as critical, whether an update stored critical or not", () => {
		expect(reviewReasonMessage("severity_unrecognized")).toBe(
			"Nocturne does not know the severity of this rule, so the rule shows it as critical. Check its severity and save it."
		);
	});

	it("still asks for a review for a reason it has no sentence for", () => {
		expect(reviewReasonMessage("constructor")).toBe(
			"This rule was changed by an update. Check it and save it."
		);
	});
});
