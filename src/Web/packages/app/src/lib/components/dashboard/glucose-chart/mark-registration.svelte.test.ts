import { render } from "vitest-browser-svelte";
import { describe, it, expect, vi } from "vitest";
import Harness from "./MarkRegistrationHarness.test.svelte";

/**
 * Every layerchart component registers itself with the chart on mount, and
 * unregistering splices itself out of its parent's children array by index — so
 * N sibling components cost O(N) registrations and O(N^2) teardown on each data
 * change. The glucose chart's per-treatment markers and BasalTrack's per-span
 * geometry therefore render as native SVG, which registers nothing.
 *
 * The tallies are compared across two dataset sizes: anything that registers per
 * datum makes the counts diverge, whatever the absolute numbers.
 */

async function renderAt(n: number) {
	const counter = { marks: 0, components: 0 };
	const clicked: string[] = [];
	const { container } = render(Harness, {
		counter,
		n,
		onMarkerClick: (treatmentId: string) => clicked.push(treatmentId),
	});

	// Bolus, carb and device-event markers are the clickable ones.
	await vi.waitFor(() => {
		expect(container.querySelectorAll("g.cursor-pointer").length).toBe(3 * n);
	});

	return { counter, container, clicked };
}

describe("glucose-chart mark registration", () => {
	it("registers nothing with layerchart per datum", async () => {
		const small = await renderAt(20);
		const large = await renderAt(120);

		expect(large.counter.components).toBe(small.counter.components);
		expect(large.counter.marks).toBe(small.counter.marks);

		// What survives is per-track (axis, clip paths, hatch patterns, highlight),
		// not per-datum, and none of it is a data-mode mark.
		expect(large.counter.components).toBeLessThan(50);
		expect(large.counter.marks).toBe(0);
	});

	it("keeps click handlers on bolus, carb and device-event markers", async () => {
		const { container, clicked } = await renderAt(1);

		const clickable = Array.from(
			container.querySelectorAll<SVGGElement>("g.cursor-pointer"),
		);
		for (const g of clickable) {
			g.dispatchEvent(new MouseEvent("click", { bubbles: true }));
		}

		expect(clicked).toEqual(["bolus-0", "carb-0", "event-0"]);
	});

	it("renders every label's text", async () => {
		// layerchart's <Text> takes a `value` prop and drops slot children, so the
		// labels these markers wrote as <Text>{...}</Text> rendered blank.
		const { container } = await renderAt(1);

		const labels = Array.from(container.querySelectorAll("text")).map((t) =>
			t.textContent?.trim(),
		);
		expect(labels).toContain("1.5U");
		expect(labels).toContain("Lunch");
		expect(labels).toContain("30g");
		expect(labels).toContain("0.75U/h");
		expect(labels).toContain("BASAL");
	});

	it("keeps marker glyph geometry and classes", async () => {
		const { container } = await renderAt(1);

		// Bolus override triangle (i % 3 === 0).
		const triangle = container.querySelector<SVGPolygonElement>(
			"polygon[points='0,12 -8,0 8,0']",
		);
		expect(triangle).not.toBeNull();
		expect(triangle?.getAttribute("class")).toBe(
			"opacity-90 fill-insulin-bolus hover:opacity-100 transition-opacity",
		);

		// Carb bowl.
		expect(
			container.querySelector("path[d='M -8,0 A 8,8 0 0,0 8,0 Z']"),
		).not.toBeNull();

		// Tracker pill.
		const pill = container.querySelector<SVGRectElement>("rect[rx='8']");
		expect(pill?.getAttribute("width")).toBe("48");
		expect(pill?.getAttribute("class")).toBe("opacity-90");
	});

	it("draws BasalTrack temp-basal spans and hatched steps without marks", async () => {
		const n = 12;
		const { container } = await renderAt(n);

		// One rect per temp-basal span.
		const spanRects = Array.from(
			container.querySelectorAll<SVGRectElement>(
				"rect[fill='var(--color-insulin-basal)']",
			),
		);
		expect(spanRects.length).toBe(n);
		expect(Number(spanRects[0].getAttribute("height"))).toBeGreaterThan(0);
		expect(Number(spanRects[0].getAttribute("width"))).toBeGreaterThan(0);

		// Labels alternate rate/percent across the spans.
		const labels = Array.from(
			container.querySelectorAll("text.fill-insulin-basal"),
		).map((t) => t.textContent?.trim());
		expect(labels).toContain("0.75U/h");
		expect(labels).toContain("120%");

		// Inferred basal draws one fill rect per step, under a single <pattern>
		// per segment rather than one per step.
		expect(
			container.querySelectorAll("rect[fill='insulin-temp-basal']")
				.length,
		).toBe(n - 1);
		expect(container.querySelectorAll("pattern").length).toBeLessThanOrEqual(2);
	});
});
