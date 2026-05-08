import { render } from "vitest-browser-svelte";
import { describe, it, expect } from "vitest";
import CornerSlot from "./CornerSlot.svelte";
import { HaloDialCornerElement } from "../config";

describe("CornerSlot", () => {
	it("renders a stack of Direction, Eventual, and LoopLabel", () => {
		const { container } = render(CornerSlot, {
			position: "br",
			elements: [
				HaloDialCornerElement.Direction,
				HaloDialCornerElement.Eventual,
				HaloDialCornerElement.LoopLabel,
			],
			data: {
				direction: { direction: "Flat" },
				eventual: { mgdl: 132, minutesAhead: 30 },
				loop: { status: "closed" },
			},
			elementConfig: {},
		});
		expect(container.querySelector("[data-testid='direction']")).not.toBeNull();
		expect(container.querySelector("[data-testid='eventual']")).not.toBeNull();
		expect(
			container.querySelector("[data-testid='loop-label']"),
		).not.toBeNull();
		const wrapper = container.querySelector("[data-testid='corner-slot']");
		expect(wrapper!.textContent).toContain("stable");
		expect(wrapper!.textContent).toContain("132");
		expect(wrapper!.textContent).toContain("closed");
	});

	it.each([
		["tl", "text-left"],
		["bl", "text-left"],
		["tr", "text-right"],
		["br", "text-right"],
	] as const)("aligns %s as %s", (position, expectedClass) => {
		const { container } = render(CornerSlot, {
			position,
			elements: [HaloDialCornerElement.Reservoir],
			data: {
				reservoir: { units: 120, percent: 60, minutesRemaining: 800 },
			},
			elementConfig: {},
		});
		const wrapper = container.querySelector("[data-testid='corner-slot']");
		expect(wrapper).not.toBeNull();
		expect(wrapper!.className).toContain(expectedClass);
	});

	it("renders an empty wrapper when no elements are configured", () => {
		const { container } = render(CornerSlot, {
			position: "tl",
			elements: [],
			data: {},
			elementConfig: {},
		});
		const wrapper = container.querySelector("[data-testid='corner-slot']");
		expect(wrapper).not.toBeNull();
		expect(wrapper!.children.length).toBe(0);
	});
});
