import { describe, it, expect } from "vitest";
import { conditionIssuesMessage, storedConditionIssuesMessage } from "./conditionIssues.svelte";
import type { RustValidationIssue } from "@nocturne/api-client";

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
					issue("list_empty", "conditions"),
					issue("minutes_not_positive", "minutes"),
					issue("list_empty", "conditions"),
				])
			)
		).toBe(
			"Every group needs at least one condition. A “for at least” duration must be 1 minute or more."
		);
	});

	it("says what is wrong with a limit from a fact", () => {
		expect(conditionIssuesMessage(rejected(400, [issue("limit_conflict", "value_from")]))).toBe(
			"A glucose condition can compare with a number or with a value such as your target, not both."
		);
		expect(conditionIssuesMessage(rejected(400, [issue("self_reference", "value_from")]))).toBe(
			"A glucose threshold cannot compare the latest glucose with itself. Choose a number or a target."
		);
		expect(conditionIssuesMessage(rejected(400, [issue("offset_out_of_range", "offset")]))).toBe(
			"The amount added to a target or to the latest glucose is too large."
		);
		expect(conditionIssuesMessage(rejected(400, [issue("unknown_value", "fact")]))).toBe(
			"A glucose condition compares with a value that Nocturne does not know. Choose one from the list and save again."
		);
	});

	it("prefers the field-specific sentence", () => {
		expect(
			conditionIssuesMessage(rejected(400, [issue("invalid_field", "alert_id")]))
		).toBe("An alert-state condition needs a rule chosen.");
		expect(
			conditionIssuesMessage(rejected(400, [issue("invalid_field", "timezone")]))
		).toBe(
			"A time-of-day condition has a time zone that is not recognized. Pick one from its time zone list, or clear it to use the profile's, and save again."
		);
		expect(
			conditionIssuesMessage(rejected(400, [issue("invalid_field", "value")]))
		).toBe("Part of this rule's conditions is malformed, so it cannot be saved.");
	});

	it("has a sentence for every reason the engine reports", () => {
		for (const [reason, field] of [
			["unknown_field", undefined],
			["field_missing", "value"],
			["field_missing", "operator"],
			["field_missing", "direction"],
			["field_missing", "state"],
			["field_missing", "child"],
			["unknown_value", "bucket"],
			["unknown_value", "operator"],
			["unknown_value", "mode"],
			["list_empty", "conditions"],
			["invalid_time", "from"],
			["empty_window", undefined],
			["list_empty", "days"],
			["pump_mode_category", "category"],
			["minutes_not_positive", "timeout_minutes"],
			["minutes_not_positive", "within_minutes"],
			["minutes_negative", "minutes"],
			["margin_negative", "clear_margin"],
			["margin_on_equality", "clear_margin"],
			["margin_past_zero", "clear_margin"],
			["margin_under_sustained", "clear_margin"],
			["selector_conflict", "min_of"],
			["selector_conflict", "max_of"],
			["list_empty", "min_of"],
			["list_empty", "max_of"],
			["unknown_value", "curve"],
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
			conditionIssuesMessage(rejected(400, [issue("margin_negative", "clear_margin")]))
		).toBe("A clear margin must be 0 or more.");
		expect(
			conditionIssuesMessage(rejected(400, [issue("invalid_field", "clear_margin")]))
		).toBe("A clear margin must be a number, 0 or more.");
		expect(
			conditionIssuesMessage(rejected(400, [issue("margin_past_zero", "clear_margin")]))
		).toBe("A clear margin on a rate of change cannot be more than the rate.");
		expect(
			conditionIssuesMessage(rejected(400, [issue("margin_under_sustained", "clear_margin")]))
		).toBe(
			"A condition inside “for at least” cannot have a clear margin. Remove the margin or the “for at least”."
		);
		expect(
			conditionIssuesMessage(
				rejected(400, [issue("field_missing", "from"), issue("field_missing", "to")])
			)
		).toBe("A time-of-day condition needs both a start and an end time.");
		expect(conditionIssuesMessage(rejected(400, [issue("selector_conflict", "max_of")]))).toBe(
			"A predicted glucose condition can use one forecast choice only: one curve, the lowest of some curves, or the highest of some curves."
		);
		expect(conditionIssuesMessage(rejected(400, [issue("list_empty", "min_of")]))).toBe(
			"A “lowest of” forecast needs at least one curve chosen."
		);
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

describe("storedConditionIssuesMessage", () => {
	it("says nothing about a stored rule with no issues", () => {
		expect(storedConditionIssuesMessage(undefined)).toBeNull();
		expect(storedConditionIssuesMessage([])).toBeNull();
	});

	it("describes every issue, including ones it has no sentence for", () => {
		expect(
			storedConditionIssuesMessage([issue("field_missing", "value"), issue("constructor")])
		).toBe(
			"A condition is missing a setting it needs. Fill it in and save again. Part of this rule's conditions is malformed, so the rule cannot run as set. Edit its conditions and save it."
		);
	});
});
