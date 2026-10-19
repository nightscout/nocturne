import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import CalendarMonthSummary from "./CalendarMonthSummary.svelte";

const summary = {
	dayCount: 20,
	totalReadings: 5000,
	inRangePercent: 70,
	lowPercent: 2,
	highPercent: 28,
	avgGlucose: 140,
};

describe("CalendarMonthSummary", () => {
	it("renders the backend TDD and average daily carbs", async () => {
		render(CalendarMonthSummary, {
			monthSummary: { ...summary, tdd: 41.25, avgDailyCarbs: 187.6 },
			units: "mg/dl",
			unitLabel: "mg/dL",
		});

		await expect.element(page.getByText("41.3U")).toBeVisible();
		await expect.element(page.getByText("188g")).toBeVisible();
	});

	it("reads n/a for figures the backend left null", async () => {
		render(CalendarMonthSummary, {
			monthSummary: { ...summary, tdd: undefined, avgDailyCarbs: undefined },
			units: "mg/dl",
			unitLabel: "mg/dL",
		});

		await expect.element(page.getByText("Avg TDD")).toBeVisible();
		expect(page.getByText("n/a").elements()).toHaveLength(2);
	});

	it("renders nothing when the month has no readings", async () => {
		render(CalendarMonthSummary, {
			monthSummary: { ...summary, totalReadings: 0, tdd: 10, avgDailyCarbs: 50 },
			units: "mg/dl",
			unitLabel: "mg/dL",
		});

		expect(page.getByText("Avg TDD").elements()).toHaveLength(0);
	});
});
