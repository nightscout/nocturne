import { describe, it, expect } from "vitest";
import {
	TrackerCategory,
	TrackerMode,
	type TrackerDefinitionDto,
} from "@nocturne/api-client";
import { ALERT_PRESETS } from "#lib/test-stubs/alert-presets.js";
import { glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
import { buildBody, parseRule } from "../types";
import {
	offeredTemplates as offeredFrom,
	draftCondition,
	draftIssue,
	newDraft as draftOf,
	suggestName,
	templateDescription,
	templateLabel,
	templatesFrom,
	templateTrackers as trackersOf,
	type TemplateId,
} from "./templates.svelte";

const TEMPLATES = templatesFrom(ALERT_PRESETS);

function template(id: TemplateId) {
	return TEMPLATES.find((t) => t.id === id)!;
}

function newDraft(id: TemplateId, definitions: TrackerDefinitionDto[] = []) {
	return draftOf(template(id), definitions);
}

function templateTrackers(id: TemplateId, definitions: TrackerDefinitionDto[]) {
	return trackersOf(template(id), definitions);
}

function offeredTemplates(definitions: TrackerDefinitionDto[]) {
	return offeredFrom(TEMPLATES, definitions);
}

const sensorTracker: TrackerDefinitionDto = {
	id: "11111111-1111-1111-1111-111111111111",
	name: "Dexcom G7",
	category: TrackerCategory.Sensor,
	mode: TrackerMode.Duration,
	lifespanHours: 252,
};
const podTracker: TrackerDefinitionDto = {
	id: "22222222-2222-2222-2222-222222222222",
	name: "Pod",
	category: TrackerCategory.Pod,
	mode: TrackerMode.Duration,
	lifespanHours: 72,
};
const appointment: TrackerDefinitionDto = {
	id: "33333333-3333-3333-3333-333333333333",
	name: "Clinic",
	category: TrackerCategory.Appointment,
	mode: TrackerMode.Event,
};
const batteryTracker: TrackerDefinitionDto = {
	id: "44444444-4444-4444-4444-444444444444",
	name: "Pump Battery",
	category: TrackerCategory.Battery,
	mode: TrackerMode.Duration,
	lifespanHours: 504,
};
const definitions = [appointment, podTracker, sensorTracker, batteryTracker];

function bodyOf(draft: ReturnType<typeof newDraft>) {
	return buildBody({ ...parseRule(null), condition: draftCondition(draft) });
}

describe("alert wizard templates", () => {
	it("lists the presets it has copy for, in their order, and then the custom type", () => {
		const templates = templatesFrom([
			ALERT_PRESETS[1],
			{ ...ALERT_PRESETS[0], id: "a_preset_from_a_later_api" },
			ALERT_PRESETS[0],
		]);
		expect(templates.map((t) => t.id)).toEqual(["low", "urgent_low", "custom"]);
	});

	it("marks which types close on the opposite of their condition", () => {
		const closing = TEMPLATES.filter((t) => t.autoResolve).map((t) => t.id);
		expect(closing).toEqual([
			"urgent_low",
			"low",
			"high",
			"reservoir_low",
			"pump_battery_low",
			"battery_change_due",
			"site_change_due",
			"sensor_change_due",
			"phone_battery_low",
		]);
	});

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

	it("reads the sensor's age from the sensor tracker, at the tracker's lifespan", () => {
		const draft = newDraft("sensor_change_due", definitions);

		const body = bodyOf(draft);

		expect(body.conditionType).toBe("tracker_age");
		expect(body.conditionParams).toEqual({
			tracker_definition_id: sensorTracker.id,
			operator: ">=",
			minutes: 252 * 60,
		});
		expect(suggestName(draft, definitions)).toBe("Dexcom G7 change due: 10.5 days");
		expect(suggestName(draft)).toBe("Sensor change due: 10.5 days");
		expect(draftIssue(draft)).toBeNull();
	});

	it("reads the site's age from a pod tracker", () => {
		expect(templateTrackers("site_change_due", definitions)).toEqual([podTracker]);
		expect(suggestName(newDraft("site_change_due", definitions), definitions)).toBe(
			"Pod change due: 3 days",
		);
		expect(bodyOf(newDraft("site_change_due", definitions)).conditionParams).toMatchObject({
			tracker_definition_id: podTracker.id,
			minutes: 72 * 60,
		});
	});

	it("alerts at the type's own age on a tracker that has no lifespan", () => {
		const draft = newDraft("sensor_change_due", [{ ...sensorTracker, lifespanHours: undefined }]);
		expect(draft.leaf?.tracker_age?.minutes).toBe(10 * 24 * 60);
	});

	it("reads the uploader's sensor start when there is no sensor tracker", () => {
		const draft = newDraft("sensor_change_due", [appointment, podTracker]);
		expect(bodyOf(draft).conditionType).toBe("sensor_age");
	});

	it("gives a type that is not an age no tracker", () => {
		expect(templateTrackers("low", definitions)).toEqual([]);
		expect(bodyOf(newDraft("low", definitions)).conditionType).toBe("threshold");
	});

	it("does not accept a tracker age of zero", () => {
		const draft = newDraft("sensor_change_due", definitions);
		draft.leaf!.tracker_age!.minutes = 0;
		expect(draftIssue(draft)).toBe("Enter a number above 0.");
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

	it("names a rate alert with the fraction of its rate in either unit", () => {
		const draft = newDraft("falling_fast");
		draft.leaf!.rate_of_change!.rate = 1.5;
		expect(suggestName(draft)).toBe("Falling fast: 1.5 mg/dL per minute");

		glucoseUnits.current = "mmol";
		try {
			expect(suggestName(draft)).toBe("Falling fast: 0.08 mmol/L per minute");
		} finally {
			glucoseUnits.current = "mg/dl";
		}
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
		for (const offered of offeredTemplates(definitions)) {
			expect(draftIssue(newDraft(offered.id, definitions)), offered.id).toBeNull();
		}
		for (const offered of offeredTemplates([])) {
			expect(draftIssue(newDraft(offered.id)), offered.id).toBeNull();
		}
	});

	it("offers the age of the battery only when there is a battery tracker", () => {
		const ids = (list: TrackerDefinitionDto[]) => offeredTemplates(list).map((t) => t.id);
		expect(ids([sensorTracker])).not.toContain("battery_change_due");
		expect(ids(definitions)).toContain("battery_change_due");

		const draft = newDraft("battery_change_due", definitions);
		expect(bodyOf(draft).conditionParams).toEqual({
			tracker_definition_id: batteryTracker.id,
			operator: ">=",
			minutes: 504 * 60,
		});
		expect(suggestName(draft, definitions)).toBe("Pump Battery change due: 21 days");
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
