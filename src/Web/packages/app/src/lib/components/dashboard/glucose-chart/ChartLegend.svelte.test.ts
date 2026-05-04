import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import ChartLegend from "./ChartLegend.svelte";

function createDefaultProps(
	overrides: Partial<Parameters<typeof ChartLegend>[1]> = {},
) {
	return {
		glucoseData: [] as { sgv: number }[],
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
		deviceEventMarkers: [] as { eventType?: string }[],
		systemEvents: [] as { id?: string; eventType?: string; color?: string }[],
		pumpModeSpans: [] as { state?: string; color?: string }[],
		scheduledTrackerMarkers: [] as { id?: string }[],
		currentPumpMode: undefined as string | undefined,
		uniquePumpModes: [] as (string | undefined)[],
		expandedPumpModes: false,
		onToggleExpandedPumpModes: vi.fn(),
		...overrides,
	};
}

describe("ChartLegend", () => {
	it("always shows 'In Range' indicator", async () => {
		render(ChartLegend, createDefaultProps());

		await expect
			.element(page.getByText("In Range"))
			.toBeVisible();
	});

	it("shows 'Very High' when glucose data has values above veryHighThreshold", async () => {
		render(
			ChartLegend,
			createDefaultProps({
				glucoseData: [{ sgv: 300 }, { sgv: 120 }],
			}),
		);

		await expect
			.element(page.getByText("Very High"))
			.toBeVisible();
	});

	it("does not show 'Very High' when no values exceed veryHighThreshold", async () => {
		render(
			ChartLegend,
			createDefaultProps({
				glucoseData: [{ sgv: 200 }, { sgv: 120 }],
			}),
		);

		await expect
			.element(page.getByText("Very High"))
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
					{ id: "1", eventType: "Alarm", color: "red" },
					{ id: "2", eventType: "Alarm", color: "red" },
				],
			}),
		);

		await expect
			.element(page.getByText("Alarms (2)"))
			.toBeVisible();
	});
});
