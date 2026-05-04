import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import DailyInsulinSummary from "./DailyInsulinSummary.svelte";

describe("DailyInsulinSummary", () => {
	const full_summary = {
		totals: {
			insulin: { bolus: 15.5, basal: 20.3 },
			food: { carbs: 180, protein: 60, fat: 45 },
		},
	};

	it("renders the Daily Summary heading", async () => {
		render(DailyInsulinSummary, { treatmentSummary: full_summary });

		await expect.element(page.getByText("Daily Summary")).toBeVisible();
	});

	it("displays bolus insulin", async () => {
		render(DailyInsulinSummary, { treatmentSummary: full_summary });

		await expect.element(page.getByText("Bolus insulin:")).toBeVisible();
		await expect.element(page.getByText(/15\.50/)).toBeVisible();
	});

	it("displays basal insulin", async () => {
		render(DailyInsulinSummary, { treatmentSummary: full_summary });

		await expect.element(page.getByText("Total basal insulin:")).toBeVisible();
		await expect.element(page.getByText(/20\.30/)).toBeVisible();
	});

	it("displays total daily insulin as sum of bolus and basal", async () => {
		render(DailyInsulinSummary, { treatmentSummary: full_summary });

		await expect.element(page.getByText("Total daily insulin:")).toBeVisible();
		// 15.5 + 20.3 = 35.8 → "35.80"
		await expect.element(page.getByText(/35\.80/)).toBeVisible();
	});

	it("displays carb totals", async () => {
		render(DailyInsulinSummary, { treatmentSummary: full_summary });

		await expect.element(page.getByText("Total carbs:")).toBeVisible();
		await expect.element(page.getByText(/180/)).toBeVisible();
	});

	it("displays protein totals", async () => {
		render(DailyInsulinSummary, { treatmentSummary: full_summary });

		await expect.element(page.getByText("Total protein:")).toBeVisible();
	});

	it("displays fat totals", async () => {
		render(DailyInsulinSummary, { treatmentSummary: full_summary });

		await expect.element(page.getByText("Total fat:")).toBeVisible();
	});

	it("handles missing totals gracefully", async () => {
		render(DailyInsulinSummary, {
			treatmentSummary: {},
		});

		// Should show N/A or handle undefined gracefully
		await expect.element(page.getByText("Daily Summary")).toBeVisible();
	});
});
