import { render } from "vitest-browser-svelte";
import { describe, it, expect, vi } from "vitest";
import { MealPeriod } from "#lib/api/index.js";
import Harness from "./IobCobTrackHarness.test.svelte";

const baseTime = new Date("2026-01-01T12:00:00Z").getTime();
const minutes = (n: number) => new Date(baseTime + n * 60 * 1000);

const meal = { time: minutes(60), carbs: 40, treatmentId: "meal1" };
const offset = { time: minutes(90), carbs: 15, treatmentId: "meal1", isOffset: true };

function carbGlyphs(container: HTMLElement): SVGPolygonElement[] {
	return Array.from(container.querySelectorAll("polygon"));
}

describe("IobCobTrack IOB area", () => {
	it("draws a negative IOB series below the zero line", async () => {
		const iobData = [0, 30, 60, 90, 120, 150, 180].map((m) => ({ time: minutes(m), value: -1 }));
		const { container } = render(Harness, { iobData, minIob: -2, maxIob: 2 });

		// The IOB track is the bottom 18% of the 300px chart, so its zero line sits halfway down it.
		const trackTop = 300 * (1 - 0.18);
		const zeroY = trackTop + (300 - trackTop) / 2;

		await vi.waitFor(() => {
			const area = container.querySelector<SVGPathElement>("path.fill-iob-basal\\/60");
			expect(area).not.toBeNull();
			const box = area!.getBBox();
			expect(box.height).toBeGreaterThan(1);
			expect(box.y).toBeGreaterThanOrEqual(zeroY - 0.5);
			expect(box.y + box.height).toBeLessThanOrEqual(300 + 0.5);
		});
	});
});

describe("IobCobTrack carb markers", () => {
	it("renders a meal and its offset marker that share one treatmentId", async () => {
		const { container } = render(Harness, { carbMarkers: [meal, offset] });

		await vi.waitFor(() => expect(carbGlyphs(container)).toHaveLength(2));
	});

	it("opens the parent meal when the offset marker is clicked", async () => {
		const onMarkerClick = vi.fn();
		const { container } = render(Harness, { carbMarkers: [meal, offset], onMarkerClick });

		await vi.waitFor(() => expect(carbGlyphs(container)).toHaveLength(2));
		carbGlyphs(container)[1].parentElement!.dispatchEvent(
			new MouseEvent("click", { bubbles: true })
		);

		expect(onMarkerClick).toHaveBeenCalledWith("meal1");
	});

	it("renders every carb marker that has no treatmentId", async () => {
		const { container } = render(Harness, {
			carbMarkers: [
				{ time: minutes(60), carbs: 10 },
				{ time: minutes(60), carbs: 20 },
			],
		});

		await vi.waitFor(() => expect(carbGlyphs(container)).toHaveLength(2));
	});
});

describe("IobCobTrack meal names", () => {
	const names = (container: HTMLElement) =>
		Array.from(container.querySelectorAll("text"))
			.map((t) => t.textContent?.trim())
			.filter((t) => t && !/^\d+g$/.test(t));

	it("words a marker's meal period when it has no food name", async () => {
		const { container } = render(Harness, {
			carbMarkers: [{ ...meal, mealPeriod: MealPeriod.LateNight }],
		});

		await vi.waitFor(() => expect(names(container)).toContain("Late night"));
	});

	it("draws the food name in place of the meal period", async () => {
		const { container } = render(Harness, {
			carbMarkers: [{ ...meal, label: "Porridge", mealPeriod: MealPeriod.Breakfast }],
		});

		await vi.waitFor(() => expect(names(container)).toContain("Porridge"));
		expect(names(container)).not.toContain("Breakfast");
	});

	it("draws no name for a marker with neither", async () => {
		const { container } = render(Harness, { carbMarkers: [meal] });

		await vi.waitFor(() => expect(carbGlyphs(container)).toHaveLength(1));
		expect(names(container).filter((t) => !/^\d+(\.\d+)?$|^IOB\/COB$/.test(t!))).toEqual([]);
	});
});
