import { render } from "vitest-browser-svelte";
import { describe, it, expect } from "vitest";
import Eventual from "./Eventual.svelte";

describe("Eventual", () => {
	it("renders mgdl value in 'value' format", () => {
		const { container } = render(Eventual, {
			value: { mgdl: 142.6, minutesAhead: 30 },
			options: { format: "value" },
		});
		const el = container.querySelector("[data-testid='eventual']");
		expect(el!.textContent).toContain("143");
	});

	it("renders placeholder when value is null", () => {
		const { container } = render(Eventual, {
			value: null,
			options: { format: "value" },
		});
		const el = container.querySelector("[data-testid='eventual']");
		expect(el!.textContent).toContain("--");
	});

	it("renders 'in <time> <value>' for in-time-value format", () => {
		const { container } = render(Eventual, {
			value: { mgdl: 110, minutesAhead: 45 },
			options: { format: "in-time-value" },
		});
		const el = container.querySelector("[data-testid='eventual']");
		expect(el!.textContent).toContain("in 45m");
		expect(el!.textContent).toContain("110");
	});
});
