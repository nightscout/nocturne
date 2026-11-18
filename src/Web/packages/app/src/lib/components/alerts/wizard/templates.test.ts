import { describe, it, expect } from "vitest";
import { buildBody, parseRule } from "../types";
import {
	TEMPLATES,
	draftCondition,
	draftIssue,
	newDraft,
	suggestName,
	templateDescription,
	templateLabel,
} from "./templates.svelte";

function bodyOf(draft: ReturnType<typeof newDraft>) {
	return buildBody({ ...parseRule(null), condition: draftCondition(draft) });
}

describe("alert wizard templates", () => {
	it("gives every type a label and a description of its own", () => {
		const labels = TEMPLATES.map((t) => templateLabel(t.id));
		const descriptions = TEMPLATES.map((t) => templateDescription(t.id));
		expect(new Set(labels).size).toBe(TEMPLATES.length);
		expect(new Set(descriptions).size).toBe(TEMPLATES.length);
	});

	it("saves a type with no wait and no hours as its one condition", () => {
		const body = bodyOf(newDraft("low"));
		expect(body.conditionType).toBe("threshold");
		expect(body.conditionParams).toEqual({ direction: "below", value: 70 });
	});

	it("holds the condition for the wait and limits it to the hours", () => {
		const draft = newDraft("high");
		draft.waitMinutes = 30;
		draft.hours = { enabled: true, from: "22:00", to: "07:00" };

		const body = bodyOf(draft);

		expect(body.conditionType).toBe("composite");
		expect(body.conditionParams).toMatchObject({
			operator: "and",
			conditions: [
				{
					type: "sustained",
					sustained: {
						minutes: 30,
						child: { type: "threshold", threshold: { direction: "above", value: 250 } },
					},
				},
				{ type: "time_of_day", time_of_day: { from: "22:00", to: "07:00" } },
			],
		});
	});

	it("leaves the wait out for a type that has its own duration", () => {
		const draft = newDraft("no_readings");
		draft.waitMinutes = 15;
		expect(bodyOf(draft).conditionType).toBe("signal_loss");
	});

	it("names an alert for its type and level", () => {
		expect(suggestName(newDraft("low"))).toBe("Low: below 70 mg/dL");
		expect(suggestName(newDraft("no_readings"))).toBe("No readings for 20 minutes");
		expect(suggestName(newDraft("site_change_due"))).toBe("Site change due: 3 days");
	});

	it("adds the wait and the hours to the name", () => {
		const draft = newDraft("low");
		draft.waitMinutes = 15;
		expect(suggestName(draft)).toBe("Low: below 70 mg/dL for 15 minutes");

		draft.hours.enabled = true;
		expect(suggestName(draft)).toMatch(/^Low: below 70 mg\/dL for 15 minutes, .+ to .+$/);
	});

	it("names a custom alert for its conditions", () => {
		expect(suggestName(newDraft("custom"))).toBe("Glucose below 70 mg/dL");
	});

	it("accepts every type as first offered", () => {
		for (const template of TEMPLATES) {
			expect(draftIssue(newDraft(template.id)), template.id).toBeNull();
		}
	});

	it("refuses a level that is empty or out of range", () => {
		const draft = newDraft("low");
		draft.leaf!.threshold!.value = Number.NaN;
		expect(draftIssue(draft)).toContain("between 40 and 400 mg/dL");
		draft.leaf!.threshold!.value = 500;
		expect(draftIssue(draft)).toContain("between 40 and 400 mg/dL");
	});

	it("refuses hours that start and end at the same time", () => {
		const draft = newDraft("low");
		draft.hours = { enabled: true, from: "08:00", to: "08:00" };
		expect(draftIssue(draft)).toBe("The start time and the end time must be different.");
	});

	it("refuses a custom alert with no conditions", () => {
		const draft = newDraft("custom");
		draft.custom!.composite!.conditions = [];
		expect(draftIssue(draft)).toBe("Add at least one condition.");
	});
});
