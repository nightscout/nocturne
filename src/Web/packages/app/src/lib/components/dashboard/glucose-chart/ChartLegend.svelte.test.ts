import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import ChartLegend from "./ChartLegend.svelte";
import { SystemEventType } from "#lib/api/index.js";
import { glucoseUnits } from "#lib/stores/appearance-store.svelte.js";

function createDefaultProps(
	overrides: Partial<Parameters<typeof ChartLegend>[1]> = {},
): Parameters<typeof ChartLegend>[1] {
	return {
		glucoseData: [],
		highThreshold: 180,
		lowThreshold: 70,
		veryHighThreshold: 250,
		veryLowThreshold: 54,
		showBasal: true,
		showIob: true,
		showCob: true,
		showBolus: true,
		showCarbs: true,
		showPumpModes: true,
		showAlarms: true,
		showScheduledTrackers: true,
		showOverrideSpans: true,
		showProfileSpans: true,
		showActivitySpans: true,
		onToggleBasal: vi.fn(),
		onToggleIob: vi.fn(),
		onToggleCob: vi.fn(),
		onToggleBolus: vi.fn(),
		onToggleCarbs: vi.fn(),
		onTogglePumpModes: vi.fn(),
		onToggleAlarms: vi.fn(),
		onToggleScheduledTrackers: vi.fn(),
		onToggleOverrideSpans: vi.fn(),
		onToggleProfileSpans: vi.fn(),
		onToggleActivitySpans: vi.fn(),
		deviceEventMarkers: [],
		systemEvents: [],
		pumpModeSpans: [],
		scheduledTrackerMarkers: [],
		currentPumpMode: undefined,
		uniquePumpModes: [],
		expandedPumpModes: false,
		onToggleExpandedPumpModes: vi.fn(),
		...overrides,
	};
}

describe("ChartLegend", () => {
	it("always shows 'In range' indicator", async () => {
		render(ChartLegend, createDefaultProps());

		await expect
			.element(page.getByText("In range"))
			.toBeVisible();
	});

	it("shows 'Very high' when glucose data has values above veryHighThreshold", async () => {
		render(
			ChartLegend,
			createDefaultProps({
				glucoseData: [{ sgv: 300 }, { sgv: 120 }],
			}),
		);

		await expect
			.element(page.getByText("Very high"))
			.toBeVisible();
	});

	it("does not show 'Very high' when no values exceed veryHighThreshold", async () => {
		render(
			ChartLegend,
			createDefaultProps({
				glucoseData: [{ sgv: 200 }, { sgv: 120 }],
			}),
		);

		await expect
			.element(page.getByText("Very high"))
			.not.toBeInTheDocument();
	});

	it("shows core toggle labels: Basal, IOB, COB, Bolus, Carbs", async () => {
		render(ChartLegend, createDefaultProps());

		await expect.element(page.getByText("Basal")).toBeVisible();
		await expect.element(page.getByText("IOB")).toBeVisible();
		await expect.element(page.getByText("COB")).toBeVisible();
		await expect.element(page.getByText("Bolus")).toBeVisible();
		await expect.element(page.getByText("Carbs")).toBeVisible();
	});

	it("shows Overrides, Profile, Activity toggles", async () => {
		render(ChartLegend, createDefaultProps());

		await expect.element(page.getByText("Overrides")).toBeVisible();
		await expect.element(page.getByText("Profile")).toBeVisible();
		await expect.element(page.getByText("Activity")).toBeVisible();
	});

	it("shows device event legend for Sensor when SensorStart markers present", async () => {
		render(
			ChartLegend,
			createDefaultProps({
				deviceEventMarkers: [{ eventType: "SensorStart" }],
			}),
		);

		await expect
			.element(page.getByText("Sensor"))
			.toBeVisible();
	});

	it("does not show device event legends when no relevant markers", async () => {
		render(
			ChartLegend,
			createDefaultProps({
				deviceEventMarkers: [],
			}),
		);

		await expect
			.element(page.getByText("Sensor"))
			.not.toBeInTheDocument();
		await expect
			.element(page.getByText("Site"))
			.not.toBeInTheDocument();
		await expect
			.element(page.getByText("Reservoir"))
			.not.toBeInTheDocument();
		await expect
			.element(page.getByText("Battery"))
			.not.toBeInTheDocument();
	});

	it("shows alarm count when system events are present", async () => {
		render(
			ChartLegend,
			createDefaultProps({
				systemEvents: [
					{ id: "1", eventType: SystemEventType.Alarm, color: "red" },
					{ id: "2", eventType: SystemEventType.Alarm, color: "red" },
				],
			}),
		);

		await expect
			.element(page.getByText("Alarms (2)"))
			.toBeVisible();
	});

	it("names each range's printed bounds in the units it is given, not the viewer's", async () => {
		glucoseUnits.current = "mg/dl";
		render(
			ChartLegend,
			createDefaultProps({
				glucoseUnits: "mmol",
				glucoseData: [{ sgv: 300 }, { sgv: 200 }, { sgv: 60 }, { sgv: 40 }],
			}),
		);

		for (const bounds of ["3.9–10", ">13.9", ">10", "<3.9", "<3", "Range limits 3.9 / 10"]) {
			await expect.element(page.getByText(bounds, { exact: true })).toBeInTheDocument();
		}
		await expect.element(page.getByText("70–180", { exact: true })).not.toBeInTheDocument();
	});
});
