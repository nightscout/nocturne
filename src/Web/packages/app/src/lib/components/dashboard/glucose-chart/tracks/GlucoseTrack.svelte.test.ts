import { render } from "vitest-browser-svelte";
import { describe, it, expect } from "vitest";
import Harness from "./GlucoseTrackHarness.test.svelte";

/**
 * These tests lock down the rendering contract of GlucoseTrack:
 *   - threshold/continuous line modes emit a stroke gradient with 10 stops
 *   - single-color mode emits no stroke gradient
 *   - area modes emit an additional fill gradient
 *   - point rendering can be force-disabled via showPoints={false}
 *
 * Counts of <linearGradient> and <stop> elements are asserted (not just
 * presence), so a regression that emits an extra gradient or fewer stops
 * is caught.
 */

function gradients(container: HTMLElement): NodeListOf<SVGLinearGradientElement> {
	return container.querySelectorAll("linearGradient");
}

function stopsIn(grad: SVGLinearGradientElement): NodeListOf<SVGStopElement> {
	return grad.querySelectorAll("stop");
}

describe("GlucoseTrack rendering contract", () => {
	it("threshold + areaMode=off renders exactly one stroke gradient with 10 stops", async () => {
		const { container } = render(Harness, {
			lineColorMode: "threshold",
			areaMode: "off",
			showPoints: false,
		});

		const grads = gradients(container);
		expect(grads.length).toBe(1);
		expect(stopsIn(grads[0]).length).toBe(10);
	});

	it("threshold + areaMode=baseline renders two gradients (stroke + fill)", async () => {
		const { container } = render(Harness, {
			lineColorMode: "threshold",
			areaMode: "baseline",
			showPoints: false,
		});

		const grads = gradients(container);
		expect(grads.length).toBe(2);
	});

	it("single + areaMode=off renders zero gradients", async () => {
		const { container } = render(Harness, {
			lineColorMode: "single",
			areaMode: "off",
			showPoints: false,
		});

		expect(gradients(container).length).toBe(0);
	});

	it("single + areaMode=deviation renders one fill-only gradient", async () => {
		const { container } = render(Harness, {
			lineColorMode: "single",
			areaMode: "deviation",
			showPoints: false,
		});

		expect(gradients(container).length).toBe(1);
	});

	it("continuous mode renders one gradient with 10 spectrum stops", async () => {
		const { container } = render(Harness, {
			lineColorMode: "continuous",
			areaMode: "off",
			showPoints: false,
		});

		const grads = gradients(container);
		expect(grads.length).toBe(1);
		expect(stopsIn(grads[0]).length).toBe(10);
	});

	it("showPoints={false} suppresses point circles", async () => {
		const { container } = render(Harness, {
			lineColorMode: "single",
			areaMode: "off",
			showPoints: false,
		});

		// layerchart Points renders <circle> elements per data point. With
		// showPoints={false} we expect none of those data circles.
		const circles = container.querySelectorAll("circle");
		expect(circles.length).toBe(0);
	});

	it("uses pointColor as a single-colour override when pointColorMode is omitted", async () => {
		// Locks DWIM behaviour in `effectivePointMode`: passing `pointColor`
		// without `pointColorMode` flips points to "single" mode so the
		// explicit colour is actually used (otherwise it would fall through
		// to threshold/continuous and silently ignore `pointColor`).
		const { container } = render(Harness, {
			lineColorMode: "threshold",
			areaMode: "off",
			showPoints: true,
			pointColor: "rgb(100, 150, 200)",
		});

		const circles = Array.from(container.querySelectorAll("circle"));
		expect(circles.length).toBeGreaterThanOrEqual(3);
		for (const c of circles) {
			expect(c.getAttribute("fill")).toBe("rgb(100, 150, 200)");
		}
	});

	it("renders points when showPoints is explicitly true", async () => {
		// Guards against an inverted-boolean regression in
		// effectiveShowPoints' density fallback (`showPoints ?? density < 0.5`).
		const { container } = render(Harness, {
			lineColorMode: "single",
			areaMode: "off",
			showPoints: true,
		});

		const circles = container.querySelectorAll("circle");
		expect(circles.length).toBeGreaterThanOrEqual(3);
	});
});
