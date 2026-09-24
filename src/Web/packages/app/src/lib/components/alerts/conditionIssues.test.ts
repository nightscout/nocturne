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
		expect(conditionIssuesMessage(rejected(400, "invalid_field:timezone"))).toBe(
			"A time-of-day condition has a time zone that isn't recognised. Pick one from its time zone list, or clear it to use the profile's, and save again."
		);
		expect(conditionIssuesMessage(rejected(400, "invalid_field:value"))).toBe(
			"Part of this rule's conditions is malformed, so it can't be saved."
		);
	});

	it("has a sentence for every save-only reason the engine reports", () => {
		for (const code of [
			"unknown_field",
			"field_missing:value",
			"unknown_value:mode",
			"invalid_time:from",
			"empty_window",
			"list_empty:days",
			"pump_mode_category:category",
		]) {
			expect(conditionIssuesMessage(rejected(400, code)), code).not.toBeNull();
		}
		expect(conditionIssuesMessage(rejected(400, "list_empty:buckets"))).toBe(
			"A glucose bucket condition needs at least one bucket chosen."
		);
		expect(conditionIssuesMessage(rejected(400, "field_missing:from, field_missing:to"))).toBe(
			"A time-of-day condition needs both a start and an end time."
		);
	});

	it("leaves other rejections to the caller", () => {
		expect(conditionIssuesMessage(rejected(400, "Unknown tracker definition."))).toBeNull();
		expect(conditionIssuesMessage(rejected(400, "conditions_empty; something else"))).toBeNull();
		expect(conditionIssuesMessage(rejected(500, "conditions_empty"))).toBeNull();
		expect(conditionIssuesMessage(new Error("offline"))).toBeNull();
	});
});
