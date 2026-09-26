import { describe, it, expect } from "vitest";
import { conditionIssuesMessage } from "./conditionIssues.svelte";
import type { RustValidationIssue } from "$api-clients";

const rejected = (status: number, issues: RustValidationIssue[]) => ({
	status,
	body: { message: "The rule's conditions cannot be saved.", issues },
});

const issue = (reason: string, field?: string): RustValidationIssue => ({
	scope: "condition",
	path: "root",
	reason,
	field,
});

describe("conditionIssuesMessage", () => {
	it("turns a condition rejection into sentences, once each", () => {
		expect(
			conditionIssuesMessage(
				rejected(400, [
					issue("conditions_empty"),
					issue("minutes_not_positive", "minutes"),
					issue("conditions_empty"),
				])
			)
		).toBe(
			"Every group needs at least one condition. A “for at least” duration must be 1 minute or more."
		);
	});

	it("prefers the field-specific sentence", () => {
		expect(
			conditionIssuesMessage(rejected(400, [issue("invalid_field", "alert_id")]))
		).toBe("An alert-state condition needs a rule chosen.");
		expect(
			conditionIssuesMessage(rejected(400, [issue("invalid_field", "timezone")]))
		).toBe(
			"A time-of-day condition has a time zone that isn't recognised. Pick one from its time zone list, or clear it to use the profile's, and save again."
		);
		expect(
			conditionIssuesMessage(rejected(400, [issue("invalid_field", "value")]))
		).toBe("Part of this rule's conditions is malformed, so it can't be saved.");
	});

	it("has a sentence for every save-only reason the engine reports", () => {
		for (const [reason, field] of [
			["unknown_field", undefined],
			["field_missing", "value"],
			["unknown_value", "mode"],
			["invalid_time", "from"],
			["empty_window", undefined],
			["list_empty", "days"],
			["pump_mode_category", "category"],
			["minutes_not_positive", "timeout_minutes"],
			["minutes_not_positive", "within_minutes"],
			["minutes_negative", "minutes"],
		] as const) {
			expect(
				conditionIssuesMessage(rejected(400, [issue(reason, field)])),
				`${reason}:${field}`
			).not.toBeNull();
		}
		expect(
			conditionIssuesMessage(rejected(400, [issue("list_empty", "buckets")]))
		).toBe("A glucose bucket condition needs at least one bucket chosen.");
		expect(
			conditionIssuesMessage(
				rejected(400, [issue("field_missing", "from"), issue("field_missing", "to")])
			)
		).toBe("A time-of-day condition needs both a start and an end time.");
	});

	it("leaves other rejections to the caller", () => {
		expect(
			conditionIssuesMessage(rejected(400, [issue("Unknown tracker definition")]))
		).toBeNull();
		expect(conditionIssuesMessage(rejected(400, []))).toBeNull();
		expect(conditionIssuesMessage({ status: 500, body: { message: "Boom" } })).toBeNull();
		expect(conditionIssuesMessage(new Error("offline"))).toBeNull();
	});

	it("does not read a prototype key as a reason it has copy for", () => {
		expect(conditionIssuesMessage(rejected(400, [issue("constructor")]))).toBeNull();
	});
});
