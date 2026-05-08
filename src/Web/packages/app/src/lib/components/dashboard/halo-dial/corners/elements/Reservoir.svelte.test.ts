import { render } from "vitest-browser-svelte";
import { describe, it, expect } from "vitest";
import Reservoir from "./Reservoir.svelte";

describe("Reservoir", () => {
	it("renders units format", () => {
		const { container } = render(Reservoir, {
			value: { units: 42.3, percent: 60, minutesRemaining: 600 },
			options: { format: "units" },
		});
		const el = container.querySelector("[data-testid='reservoir']");
		expect(el!.textContent).toContain("42.3 U");
	});

	it("renders placeholder when value is null", () => {
		const { container } = render(Reservoir, {
			value: null,
			options: { format: "percent" },
		});
		const el = container.querySelector("[data-testid='reservoir']");
		expect(el!.textContent).toContain("--");
	});

	it("renders time-left format with hours and minutes", () => {
		const { container } = render(Reservoir, {
			value: { units: 10, percent: 25, minutesRemaining: 125 },
			options: { format: "time-left" },
		});
		const el = container.querySelector("[data-testid='reservoir']");
		expect(el!.textContent).toContain("2h 5m");
	});
});
