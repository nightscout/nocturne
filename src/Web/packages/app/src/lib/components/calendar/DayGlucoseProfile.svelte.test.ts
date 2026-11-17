import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import DayGlucoseProfile from "./DayGlucoseProfile.svelte";

const hour = 3_600_000;
const start = 1792879200000;
const longDay = { startMills: start, endMills: start + 25 * hour };
const readings = [
	{ mills: start, mgdl: 120 },
	{ mills: start + 2 * hour, mgdl: 140 },
];

async function lineRightEdge(props: Parameters<typeof render<typeof DayGlucoseProfile>>[1]) {
	const { container } = render(DayGlucoseProfile, props);
	await expect.poll(() => container.querySelector("svg path.lc-area-line")).not.toBeNull();
	const box = (container.querySelector("svg path.lc-area-line") as SVGGraphicsElement).getBBox();
	const plotWidth = container.querySelector("svg")!.getBoundingClientRect().width - 2;
	return { right: box.x + box.width, plotWidth };
}

describe("DayGlucoseProfile", () => {
	it("scales readings across the day it is given", async () => {
		const { right, plotWidth } = await lineRightEdge({
			props: { entries: readings, day: longDay },
		});

		expect(right).toBeCloseTo(plotWidth * (2 / 25), 0);
	});

	it("scales readings across the local calendar day of the first reading when no day is given", async () => {
		const localMidnight = new Date(2026, 9, 6).getTime();
		const { right, plotWidth } = await lineRightEdge({
			props: {
				entries: [
					{ mills: localMidnight, mgdl: 120 },
					{ mills: localMidnight + 2 * hour, mgdl: 140 },
				],
			},
		});

		expect(right).toBeCloseTo(plotWidth * (2 / 24), 0);
	});

	it("says there is no data when there are no readings", async () => {
		render(DayGlucoseProfile, { props: { entries: [], day: longDay } });

		await expect.element(page.getByText("No data")).toBeVisible();
	});

	it("calls onclick when the day is clicked", async () => {
		let clicks = 0;
		render(DayGlucoseProfile, {
			props: { entries: readings, day: longDay, onclick: () => clicks++ },
		});

		await page.getByRole("button").click();

		expect(clicks).toBe(1);
	});
});
