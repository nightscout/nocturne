import { render } from "vitest-browser-svelte";
import { describe, it, expect, vi } from "vitest";
import Harness from "./IobCobTrackHarness.test.svelte";

const baseTime = new Date("2026-01-01T12:00:00Z").getTime();
const minutes = (n: number) => new Date(baseTime + n * 60 * 1000);

const meal = { time: minutes(60), carbs: 40, treatmentId: "meal1" };
const offset = { time: minutes(90), carbs: 15, treatmentId: "meal1", isOffset: true };

function carbGlyphs(container: HTMLElement): SVGPolygonElement[] {
	return Array.from(container.querySelectorAll("polygon"));
}

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
