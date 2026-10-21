import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, it, expect, vi } from "vitest";
import AmbulatoryGlucoseProfile from "./AmbulatoryGlucoseProfile.svelte";
import type { AveragedStats } from "$lib/api";
import type { ChartThresholdsDto } from "$lib/api/generated/nocturne-api-client";

const realtime = vi.hoisted(() => ({
	chartThresholds: null as ChartThresholdsDto | null,
}));

vi.mock("$lib/stores/realtime-store.svelte", async () => {
	const { resolveChartThresholds } = await import("$lib/constants/glucose-thresholds");
	return {
		tenantChartThresholds: () => ({
			get current() {
				return resolveChartThresholds(realtime.chartThresholds);
			},
		}),
	};
});

// layerchart's AreaChart throws effect_orphan under vitest-browser-svelte, so the
// stub stands in for it and exposes the props the component derives from thresholds.
vi.mock("layerchart", async (importOriginal) => ({
	...(await importOriginal<typeof import("layerchart")>()),
	AreaChart: (await import("./AreaChartStub.test.svelte")).default,
}));

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

	it("renders chart when data is provided", async () => {
		const stats = makeStats([0, 6, 12, 18]);

		render(AmbulatoryGlucoseProfile, { averagedStats: stats });

		await expect.element(page.getByTestId("agp-chart")).toBeVisible();
		await expect
			.element(page.getByText("No pattern data"))
			.not.toBeInTheDocument();
	});

	describe("thresholds", () => {
		beforeEach(() => {
			realtime.chartThresholds = null;
		});

		it("draws the shared fallback range and axis ceiling when the store has none", async () => {
			render(AmbulatoryGlucoseProfile, { averagedStats: makeStats([0, 6, 12, 18]) });

			await expect.element(page.getByText("Low 70")).toBeVisible();
			await expect.element(page.getByText("High 180")).toBeVisible();
			await expect.element(page.getByText(/Target range 70\D+180/)).toBeVisible();
			await expect
				.element(page.getByTestId("agp-chart"))
				.toHaveAttribute("data-y-max", "400");
		});

		it("draws the tenant's range and axis ceiling from the store", async () => {
			realtime.chartThresholds = {
				veryLow: 60,
				low: 80,
				high: 160,
				veryHigh: 220,
				glucoseYMax: 300,
			};

			render(AmbulatoryGlucoseProfile, { averagedStats: makeStats([0, 6, 12, 18]) });

			await expect.element(page.getByText("Low 80")).toBeVisible();
			await expect.element(page.getByText("High 160")).toBeVisible();
			await expect.element(page.getByText(/Target range 80\D+160/)).toBeVisible();
			await expect
				.element(page.getByTestId("agp-chart"))
				.toHaveAttribute("data-y-max", "300");
		});
	});
});
