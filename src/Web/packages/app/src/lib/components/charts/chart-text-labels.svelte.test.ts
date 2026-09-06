import { render } from "vitest-browser-svelte";
import { describe, it, expect, vi } from "vitest";
import Harness from "./ChartTextLabelHarness.test.svelte";
import BasalRateChart from "$lib/components/reports/BasalRateChart.svelte";
import GlucoseResponseChart from "$lib/components/dashboard/glucose-chart/dialogs/GlucoseResponseChart.svelte";
import type { PredictionData } from "$api/predictions.remote";

/**
 * layerchart's <Text> renders its `value` prop and ignores snippet children, so
 * every label written as `<Text>{expr}</Text>` painted an empty string. These
 * assertions read the rendered text back out of the DOM, per label.
 */

function texts(container: HTMLElement): string[] {
	return Array.from(container.querySelectorAll("text")).map(
		(t) => t.textContent?.trim() ?? "",
	);
}

async function textsIn(container: HTMLElement, expected: string) {
	await vi.waitFor(() => {
		expect(texts(container)).toContain(expected);
	});
}

describe("chart track labels", () => {
	it("renders the BASAL label on the basal rate track", async () => {
		const { container } = render(Harness, { track: "basalRate" });
		await textsIn(container, "BASAL");
	});

	it("renders the IOB/COB label on the IOB/COB track", async () => {
		const { container } = render(Harness, { track: "iobCob" });
		await textsIn(container, "IOB/COB");
	});
});

describe("prediction overlay labels", () => {
	it("renders the prediction error label", async () => {
		const { container } = render(Harness, {
			track: "predictions",
			predictionError: "upstream down",
		});
		await textsIn(container, "Prediction unavailable");
	});

	it("renders the boundary failure label with the thrown message", async () => {
		const { container } = render(Harness, {
			track: "predictions",
			predictionData: {
				timestamp: new Date("2026-01-01T00:00:00Z"),
				currentBg: 120,
				delta: 0,
				eventualBg: 140,
				iob: 1,
				cob: 20,
				sensitivityRatio: null,
				intervalMinutes: 5,
				curves: {
					main: [{ timestamp: Date.parse("2026-01-01T00:05:00Z"), value: 140 }],
					iobOnly: [],
					uam: [],
					cob: [],
					zeroTemp: [],
				},
			} satisfies PredictionData,
			glucoseScale: () => {
				throw new Error("scale exploded");
			},
		});

		await textsIn(container, "Prediction unavailable: scale exploded");
	});
});

describe("BasalRateChart labels", () => {
	const xDomain: [Date, Date] = [
		new Date("2026-01-01T00:00:00Z"),
		new Date("2026-01-01T06:00:00Z"),
	];
	const data = [
		{ time: xDomain[0], rate: 0.6 },
		{ time: new Date("2026-01-01T03:00:00Z"), rate: 1.2 },
	];

	it("renders the BASAL label and the default-rate readout", async () => {
		const { container } = render(BasalRateChart, {
			data,
			xDomain,
			defaultRate: 0.85,
			showDefaultLine: true,
		});

		await textsIn(container, "BASAL");
		await textsIn(container, "0.85 U/hr");
	});

	it("drops the default-rate readout when the reference line is hidden", async () => {
		const { container } = render(BasalRateChart, {
			data,
			xDomain,
			defaultRate: 0.85,
			showDefaultLine: false,
		});

		await textsIn(container, "BASAL");
		expect(texts(container)).not.toContain("0.85 U/hr");
	});

	it("positions the default-rate readout inside the plot area", async () => {
		// The label used to take the raw rate as a pixel y, which pinned it a
		// fraction of a pixel from the chart top regardless of the y-domain.
		const { container } = render(BasalRateChart, {
			data,
			xDomain,
			defaultRate: 0.85,
			showDefaultLine: true,
		});

		await textsIn(container, "0.85 U/hr");
		const label = Array.from(container.querySelectorAll("text")).find(
			(t) => t.textContent?.trim() === "0.85 U/hr",
		);
		expect(Number(label?.getAttribute("y"))).toBeGreaterThan(10);
	});
});

describe("GlucoseResponseChart annotations", () => {
	const glucoseData = [
		{
			time: new Date("2026-01-01T00:00:00Z"),
			sgv: 90,
			color: "var(--glucose-in-range)",
		},
		{
			time: new Date("2026-01-01T00:30:00Z"),
			sgv: 240,
			color: "var(--glucose-high)",
		},
		{
			time: new Date("2026-01-01T01:00:00Z"),
			sgv: 150,
			color: "var(--glucose-in-range)",
		},
	];

	it("renders the centre label plus peak and nadir readouts", async () => {
		const { container } = render(GlucoseResponseChart, {
			glucoseData,
			centerTime: new Date("2026-01-01T00:30:00Z"),
			highThreshold: 180,
			lowThreshold: 70,
			label: "Breakfast",
		});

		await textsIn(container, "Breakfast");
		await textsIn(container, "240");
		await textsIn(container, "90");
	});

	it("positions the annotations inside the plot area", async () => {
		// x/y used to be handed the raw epoch millis and mg/dL, which put every
		// annotation billions of pixels off canvas.
		const { container } = render(GlucoseResponseChart, {
			glucoseData,
			centerTime: new Date("2026-01-01T00:30:00Z"),
			highThreshold: 180,
			lowThreshold: 70,
			label: "Breakfast",
		});

		await textsIn(container, "Breakfast");
		const label = Array.from(container.querySelectorAll("text")).find(
			(t) => t.textContent?.trim() === "Breakfast",
		);
		expect(Number(label?.getAttribute("x"))).toBeLessThan(10_000);
		expect(Number(label?.getAttribute("y"))).toBeLessThan(10_000);
	});
});
