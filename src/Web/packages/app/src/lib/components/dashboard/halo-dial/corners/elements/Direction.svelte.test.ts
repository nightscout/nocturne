import { render } from "vitest-browser-svelte";
import { describe, it, expect } from "vitest";
import Direction from "./Direction.svelte";

describe("Direction", () => {
	it("renders 'stable' for Flat direction", () => {
		const { container } = render(Direction, {
			value: { direction: "Flat" },
		});
		const el = container.querySelector("[data-testid='direction']");
		expect(el!.textContent).toContain("stable");
	});

	it("renders placeholder when value is null", () => {
		const { container } = render(Direction, { value: null });
		const el = container.querySelector("[data-testid='direction']");
		expect(el!.textContent).toContain("--");
	});

	it("renders 'rising slowly' for FortyFiveUp", () => {
		const { container } = render(Direction, {
			value: { direction: "FortyFiveUp" },
		});
		const el = container.querySelector("[data-testid='direction']");
		expect(el!.textContent).toContain("rising slowly");
		expect(el!.getAttribute("data-direction")).toBe("FortyFiveUp");
	});

	it("renders 'unknown' for NotComputable", () => {
		const { container } = render(Direction, {
			value: { direction: "NotComputable" },
		});
		const el = container.querySelector("[data-testid='direction']");
		expect(el!.textContent).toContain("unknown");
		expect(el!.getAttribute("data-direction")).toBe("NotComputable");
	});

	it("renders 'out of range' for RateOutOfRange", () => {
		const { container } = render(Direction, {
			value: { direction: "RateOutOfRange" },
		});
		const el = container.querySelector("[data-testid='direction']");
		expect(el!.textContent).toContain("out of range");
		expect(el!.getAttribute("data-direction")).toBe("RateOutOfRange");
	});
});
