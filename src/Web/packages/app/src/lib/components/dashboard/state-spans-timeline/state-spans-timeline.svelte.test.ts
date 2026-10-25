import { render } from "vitest-browser-svelte";
import { describe, it, expect, vi } from "vitest";
import { BasalDeliveryOrigin, type BasalDeliverySpanDto } from "$lib/api";
import StateSpansTimeline from "./state-spans-timeline.svelte";

const from = new Date("2026-01-01T00:00:00Z");
const to = new Date("2026-01-01T06:00:00Z");
const hour = 60 * 60 * 1000;

function renderTimeline(tempBasalSpans: BasalDeliverySpanDto[]) {
	return render(StateSpansTimeline, {
		pumpModeSpans: [],
		profileSpans: [],
		tempBasalSpans,
		overrideSpans: [],
		activitySpans: [],
		dateRange: { from, to },
		showPumpModes: false,
		showProfiles: false,
		showTempBasals: true,
		showOverrides: false,
		showActivities: false,
	});
}

describe("state spans timeline basal lane", () => {
	it("draws an inferred span faded and drops a span outside the range", async () => {
		const { container } = renderTimeline([
			{
				id: "inferred",
				startMills: from.getTime() - hour,
				endMills: from.getTime() + 2 * hour,
				rate: 0.8,
				origin: BasalDeliveryOrigin.Inferred,
			},
			{
				id: "outside",
				startMills: to.getTime() + hour,
				endMills: to.getTime() + 2 * hour,
				rate: 1.5,
				origin: BasalDeliveryOrigin.Inferred,
			},
		]);

		await vi.waitFor(() => {
			expect(container.querySelectorAll(".opacity-40")).toHaveLength(1);
		});
	});
});
