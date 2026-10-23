import { render } from "vitest-browser-svelte";
import { describe, it, expect } from "vitest";
import type { PunchCardDay } from "$api";
import CalendarDayCellHarness from "./CalendarDayCellHarness.test.svelte";

const noop = () => {};

function renderCell(day: PunchCardDay | { empty: true; dayNumber: number }, showTirPercent: boolean) {
	return render(CalendarDayCellHarness, {
		day,
		viewMode: "tir",
		showTirPercent,
		currentYear: 2025,
		currentMonth: 5,
		trackerEvents: new Map(),
		definitions: [],
		openPopoverId: null,
		units: "mg/dl",
		unitLabel: "mg/dL",
		handleDayClick: noop,
		getDefinition: () => undefined,
		getTrackerLevel: () => "none",
		getTrackerTone: () => "none",
		formatTrackerStartTime: () => null,
		formatTrackerAge: () => "",
		openCompletionDialog: noop,
	});
}

const dayWithData = (inRangePercent: number): PunchCardDay => ({
	date: "2025-06-10",
	timestamp: 1749513600000,
	totalReadings: 288,
	veryLowPercent: 1,
	lowPercent: 4,
	inRangePercent,
	highPercent: 100 - 5 - inRangePercent,
	veryHighPercent: 0,
});

describe("CalendarDayCell TIR percentage", () => {
	it("prints the served in-range percentage, rounded, when shown", async () => {
		const { container } = renderCell(dayWithData(72.4), true);

		const label = container.querySelector("[data-testid='tir-percent']");
		expect(label?.textContent?.trim()).toBe("72%");
	});

	it("rounds to the nearest whole percent", async () => {
		const { container } = renderCell(dayWithData(84.6), true);

		expect(container.querySelector("[data-testid='tir-percent']")?.textContent?.trim()).toBe("85%");
	});

	it("prints nothing when the toggle is off", async () => {
		const { container } = renderCell(dayWithData(72.4), false);

		expect(container.querySelector("[data-testid='tir-percent']")).toBeNull();
	});

	it("prints nothing for a day with no readings", async () => {
		const { container } = renderCell({ ...dayWithData(0), totalReadings: 0 }, true);

		expect(container.querySelector("[data-testid='tir-percent']")).toBeNull();
	});

	it("prints nothing for a day without data", async () => {
		const { container } = renderCell({ empty: true, dayNumber: 3 }, true);

		expect(container.querySelector("[data-testid='tir-percent']")).toBeNull();
	});
});
