import { render } from "vitest-browser-svelte";
import { describe, it, expect } from "vitest";
import LoopLabel from "./LoopLabel.svelte";

describe("LoopLabel", () => {
	it("renders the status text", () => {
		const { container } = render(LoopLabel, {
			value: { status: "closed" },
		});
		const el = container.querySelector("[data-testid='loop-label']");
		expect(el!.textContent).toContain("closed");
	});

	it("renders placeholder when value is null", () => {
		const { container } = render(LoopLabel, { value: null });
		const el = container.querySelector("[data-testid='loop-label']");
		expect(el!.textContent).toContain("--");
	});

	it("renders 'open' status text", () => {
		const { container } = render(LoopLabel, {
			value: { status: "open" },
		});
		const el = container.querySelector("[data-testid='loop-label']");
		expect(el!.textContent).toContain("open");
	});
});
