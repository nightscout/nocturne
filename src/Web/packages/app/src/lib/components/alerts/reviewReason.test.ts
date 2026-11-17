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

	it("explains a rule turned off for an unknown condition type", () => {
		expect(reviewReasonMessage("condition_type_unrecognized")).toContain("turned off");
	});

	it("still asks for a review for a reason it has no sentence for", () => {
		expect(reviewReasonMessage("constructor")).toBe(
			"This rule was changed by an update. Check it and save it."
		);
	});
});
