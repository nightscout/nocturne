import { render } from "vitest-browser-svelte";
import { describe, it, expect } from "vitest";
import Battery from "./Battery.svelte";

describe("Battery", () => {
	it("renders percent by default", () => {
		const { container } = render(Battery, {
			value: { percent: 78 },
			options: { format: "percent" },
		});
		const el = container.querySelector("[data-testid='battery']");
		expect(el!.textContent).toContain("78%");
	});

	it("renders placeholder when value is null", () => {
		const { container } = render(Battery, {
			value: null,
			options: { format: "percent" },
		});
		const el = container.querySelector("[data-testid='battery']");
		expect(el!.textContent).toContain("--");
	});

	it("renders voltage format", () => {
		const { container } = render(Battery, {
			value: { percent: 60, voltage: 3.85 },
			options: { format: "voltage" },
		});
		const el = container.querySelector("[data-testid='battery']");
		expect(el!.textContent).toContain("3.85V");
	});
});
