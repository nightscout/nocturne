import { render } from "vitest-browser-svelte";
import { describe, it, expect } from "vitest";
import BasalRate from "./BasalRate.svelte";

describe("BasalRate", () => {
	it("renders rate in U/h by default", () => {
		const { container } = render(BasalRate, {
			value: { rate: 0.85, percent: 100 },
			options: { format: "U/h" },
		});
		const el = container.querySelector("[data-testid='basal-rate']");
		expect(el).not.toBeNull();
		expect(el!.textContent).toContain("0.85 U/h");
	});

	it("renders placeholder when value is null", () => {
		const { container } = render(BasalRate, {
			value: null,
			options: { format: "U/h" },
		});
		const el = container.querySelector("[data-testid='basal-rate']");
		expect(el!.textContent).toContain("--");
	});

	it("renders both rate and percent in 'both' format", () => {
		const { container } = render(BasalRate, {
			value: { rate: 1.2, percent: 150 },
			options: { format: "both" },
		});
		const el = container.querySelector("[data-testid='basal-rate']");
		expect(el!.textContent).toContain("1.20 U/h");
		expect(el!.textContent).toContain("150%");
	});
});
