import { render } from "vitest-browser-svelte";
import { describe, it, expect } from "vitest";
import type { PunchCardDay } from "#lib/api/index.js";
import CalendarDayCellHarness from "./CalendarDayCellHarness.test.svelte";

const noop = () => {};

function renderCell(
	day: PunchCardDay | { empty: true; dayNumber: number },
	showTirPercent: boolean,
	viewMode: "tir" | "profile" = "tir",
) {
	return render(CalendarDayCellHarness, {
		day,
		viewMode,
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

describe("CalendarDayCell profile day domain", () => {
	const hour = 3_600_000;
	const start = 1792879200000;
	const readings = [
		{ mills: start, mgdl: 120 },
		{ mills: start + 2 * hour, mgdl: 140 },
	];
	const profileDay = (endTimestamp?: number): PunchCardDay => ({
		...dayWithData(72),
		date: "2026-10-31",
		timestamp: start,
		endTimestamp,
		entries: readings,
	});

	async function lineRightEdge(day: PunchCardDay) {
		const { container } = renderCell(day, false, "profile");
		await expect.poll(() => container.querySelector("svg path.lc-area-line")).not.toBeNull();
		const box = (container.querySelector("svg path.lc-area-line") as SVGGraphicsElement).getBBox();
		const plotWidth = container.querySelector("svg")!.getBoundingClientRect().width - 2;
		return { right: box.x + box.width, plotWidth };
	}

	it("spans the sparkline over the day the backend served, from timestamp to endTimestamp", async () => {
		const { right, plotWidth } = await lineRightEdge(profileDay(start + 25 * hour));

		expect(right).toBeCloseTo(plotWidth * (2 / 25), 0);
	});

	it("spans a 23 hour day by its real length", async () => {
		const { right, plotWidth } = await lineRightEdge(profileDay(start + 23 * hour));

		expect(right).toBeCloseTo(plotWidth * (2 / 23), 0);
	});

	it("falls back to the local calendar day of the first reading when endTimestamp is missing", async () => {
		const localMidnight = new Date(2026, 9, 6).getTime();
		const { right, plotWidth } = await lineRightEdge({
			...profileDay(undefined),
			timestamp: localMidnight,
			entries: [
				{ mills: localMidnight, mgdl: 120 },
				{ mills: localMidnight + 2 * hour, mgdl: 140 },
			],
		});

		expect(right).toBeCloseTo(plotWidth * (2 / 24), 0);
	});
});
