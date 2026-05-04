import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import AmbulatoryGlucoseProfile from "./AmbulatoryGlucoseProfile.svelte";
import type { AveragedStats } from "$lib/api";

function makeStats(hours: number[]): AveragedStats[] {
	return hours.map((hour) => ({
		hour,
		median: 140,
		percentiles: {
			p10: 80,
			p25: 100,
			p75: 180,
			p90: 220,
		},
	}));
}

describe("AmbulatoryGlucoseProfile", () => {
	it("shows empty state when no data provided", async () => {
		render(AmbulatoryGlucoseProfile);

		await expect.element(page.getByText("No pattern data")).toBeVisible();
		await expect
			.element(page.getByText("Need more readings to show your typical day"))
			.toBeVisible();
	});

	it("shows empty state with undefined averagedStats", async () => {
		render(AmbulatoryGlucoseProfile, { averagedStats: undefined });

		await expect.element(page.getByText("No pattern data")).toBeVisible();
	});

	it("shows empty state with empty array", async () => {
		render(AmbulatoryGlucoseProfile, { averagedStats: [] });

		await expect.element(page.getByText("No pattern data")).toBeVisible();
		await expect
			.element(page.getByText("Need more readings to show your typical day"))
			.toBeVisible();
	});

	// layerchart's AreaChart uses $effect.pre internally (SeriesState) which
	// throws effect_orphan in vitest-browser-svelte. Skip until layerchart
	// provides a test-friendly build or we add a wrapper stub.
	it.skip("renders chart when data is provided", async () => {
		const stats = makeStats([0, 6, 12, 18]);

		render(AmbulatoryGlucoseProfile, { averagedStats: stats });

		await expect
			.element(page.getByText("No pattern data"))
			.not.toBeInTheDocument();
	});
});
