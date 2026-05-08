import { render } from "vitest-browser-svelte";
import { describe, it, expect } from "vitest";
import { HaloDialArcElement } from "$lib/api/generated/nocturne-api-client";
import InnerArcSlot from "./InnerArcSlot.svelte";

describe("InnerArcSlot", () => {
	it("targets ~65deg sweep at 50% of max (IOB right)", () => {
		const { container } = render(InnerArcSlot, {
			element: HaloDialArcElement.Iob,
			value: 4,
			max: 8,
			side: "right",
		});

		const valuePath = container.querySelector(
			"[data-testid='inner-arc-value']",
		);
		expect(valuePath).not.toBeNull();
		const target = Number(
			valuePath!.getAttribute("data-target-sweep-deg"),
		);
		expect(target).toBeCloseTo(65, 3);
	});

	it("targets 0deg sweep when value is null", () => {
		const { container } = render(InnerArcSlot, {
			element: HaloDialArcElement.Cob,
			value: null,
			max: 80,
			side: "left",
		});

		const valuePath = container.querySelector(
			"[data-testid='inner-arc-value']",
		);
		expect(valuePath).not.toBeNull();
		const target = Number(
			valuePath!.getAttribute("data-target-sweep-deg"),
		);
		expect(target).toBe(0);
	});

	it("targets 130deg sweep when value equals max", () => {
		const { container } = render(InnerArcSlot, {
			element: HaloDialArcElement.Iob,
			value: 8,
			max: 8,
			side: "right",
		});

		const valuePath = container.querySelector(
			"[data-testid='inner-arc-value']",
		);
		expect(valuePath).not.toBeNull();
		const target = Number(
			valuePath!.getAttribute("data-target-sweep-deg"),
		);
		expect(target).toBeCloseTo(130, 3);
	});

	it("renders a full-sweep track regardless of value", () => {
		const { container } = render(InnerArcSlot, {
			element: HaloDialArcElement.Iob,
			value: 0,
			max: 8,
			side: "right",
		});

		const track = container.querySelector(
			"[data-testid='inner-arc-track']",
		);
		expect(track).not.toBeNull();
		expect(track!.getAttribute("stroke-opacity")).toBe("0.15");
		expect(track!.getAttribute("d")).toMatch(/^M /);
	});
});
