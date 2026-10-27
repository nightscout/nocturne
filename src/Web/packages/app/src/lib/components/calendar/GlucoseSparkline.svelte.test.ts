import { render } from "vitest-browser-svelte";
import { beforeEach, describe, it, expect, vi } from "vitest";
import type { ChartThresholdsDto } from "#lib/api/generated/nocturne-api-client.js";
import {
	FALLBACK_GLUCOSE_THRESHOLDS,
	FALLBACK_GLUCOSE_Y_MAX,
} from "#lib/constants/glucose-thresholds.js";
import Harness from "./GlucoseSparklineHarness.test.svelte";

const realtime = vi.hoisted(() => ({
	chartThresholds: null as ChartThresholdsDto | null,
}));

vi.mock("#lib/stores/realtime-store.svelte.js", async () => {
	const { resolveChartThresholds } = await import("#lib/constants/glucose-thresholds.js");
	return {
		tenantChartThresholds: () => ({
			get current() {
				return resolveChartThresholds(realtime.chartThresholds);
			},
		}),
	};
});

const dayStartMills = Date.UTC(2026, 9, 6);
const entries = [90, 140, 200].map((mgdl, i) => ({
	mills: dayStartMills + i * 3_600_000,
	mgdl,
}));

async function veryLowBoundaryOffset(
	thresholds?: ChartThresholdsDto,
): Promise<number> {
	const { container } = render(Harness, { entries, thresholds });
	await expect.poll(() => container.querySelectorAll("linearGradient").length).toBeGreaterThan(0);
	const stops = container.querySelector("linearGradient")!.querySelectorAll("stop");
	const veryLowStop = [...stops].find(
		(s) => s.getAttribute("stop-color") === "var(--glucose-very-low)",
	);
	return Number.parseFloat(veryLowStop?.getAttribute("offset") ?? "NaN");
}

const fallbackOffset = (veryLow: number, glucoseYMax: number) =>
	1 - veryLow / glucoseYMax;

describe("GlucoseSparkline day domain", () => {
	const hour = 3_600_000;
	const berlinDays = [
		{ name: "25 hour day", start: 1792879200000, end: 1792969200000 },
		{ name: "23 hour day", start: 1774738800000, end: 1774821600000 },
	];

	async function dataLineExtent(
		readings: Array<{ mills: number; mgdl: number }>,
		day: { startMills: number; endMills: number },
	) {
		const { container } = render(Harness, { entries: readings, day });
		await expect.poll(() => container.querySelector("svg path.lc-area-line")).not.toBeNull();
		const box = (container.querySelector("svg path.lc-area-line") as SVGGraphicsElement).getBBox();
		const plotWidth = container.querySelector("svg")!.getBoundingClientRect().width - 2;
		return { left: box.x, right: box.x + box.width, plotWidth };
	}

	for (const { name, start, end } of berlinDays) {
		it(`draws readings at the first and last millisecond of the ${name} edge to edge`, async () => {
			const { left, right, plotWidth } = await dataLineExtent(
				[
					{ mills: start, mgdl: 120 },
					{ mills: end - 1, mgdl: 140 },
				],
				{ startMills: start, endMills: end },
			);

			expect(left).toBeCloseTo(0, 0);
			expect(right).toBeCloseTo(plotWidth, 0);
		});

		it(`places a reading two hours before the end of the ${name} by the day's real length`, async () => {
			const { right, plotWidth } = await dataLineExtent(
				[
					{ mills: start, mgdl: 120 },
					{ mills: end - 2 * hour, mgdl: 140 },
				],
				{ startMills: start, endMills: end },
			);

			expect(right).toBeCloseTo(plotWidth * (1 - (2 * hour) / (end - start)), 0);
		});
	}
});

describe("GlucoseSparkline thresholds", () => {
	beforeEach(() => {
		realtime.chartThresholds = null;
	});

	it("falls back to the shared cut-points and axis ceiling when none are supplied", async () => {
		const offset = await veryLowBoundaryOffset();

		expect(offset).toBeCloseTo(
			fallbackOffset(FALLBACK_GLUCOSE_THRESHOLDS.veryLow, FALLBACK_GLUCOSE_Y_MAX),
			2,
		);
	});

	it("honours a supplied cut-point and axis ceiling", async () => {
		const offset = await veryLowBoundaryOffset({
			veryLow: 80,
			low: 100,
			high: 160,
			veryHigh: 220,
			glucoseYMax: 240,
		});

		expect(offset).toBeCloseTo(fallbackOffset(80, 240), 2);
	});

	it("moves the boundary when only the axis ceiling differs", async () => {
		const base = { ...FALLBACK_GLUCOSE_THRESHOLDS, glucoseYMax: 400 };
		const tall = await veryLowBoundaryOffset(base);
		const short = await veryLowBoundaryOffset({ ...base, glucoseYMax: 200 });

		expect(short).toBeLessThan(tall);
	});

	it("reads the tenant's thresholds from the realtime store", async () => {
		realtime.chartThresholds = {
			veryLow: 80,
			low: 100,
			high: 160,
			veryHigh: 220,
			glucoseYMax: 240,
		};

		const offset = await veryLowBoundaryOffset();

		expect(offset).toBeCloseTo(fallbackOffset(80, 240), 2);
	});
});
