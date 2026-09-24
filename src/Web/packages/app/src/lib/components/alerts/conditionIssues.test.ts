import { describe, it, expect } from "vitest";
import { conditionIssuesMessage } from "./conditionIssues.svelte";

const rejected = (status: number, message: string) => ({ status, body: { message } });

describe("conditionIssuesMessage", () => {
	it("turns a condition rejection into sentences, once each", () => {
		expect(
			conditionIssuesMessage(
				rejected(400, "conditions_empty:conditions; minutes_not_positive:minutes, conditions_empty:conditions")
			)
		).toBe(
			"Every group needs at least one condition. A “for at least” duration must be 1 minute or more."
		);
	});

	it("prefers the field-specific sentence", () => {
		expect(conditionIssuesMessage(rejected(400, "invalid_field:alert_id"))).toBe(
			"An alert-state condition needs a rule chosen."
		);
		expect(conditionIssuesMessage(rejected(400, "invalid_field:value"))).toBe(
			"Part of this rule's conditions is malformed, so it can't be saved."
		);
	});

	it("leaves other rejections to the caller", () => {
		expect(conditionIssuesMessage(rejected(400, "Unknown tracker definition."))).toBeNull();
		expect(conditionIssuesMessage(rejected(400, "conditions_empty; something else"))).toBeNull();
		expect(conditionIssuesMessage(rejected(500, "conditions_empty"))).toBeNull();
		expect(conditionIssuesMessage(new Error("offline"))).toBeNull();
	});
});
