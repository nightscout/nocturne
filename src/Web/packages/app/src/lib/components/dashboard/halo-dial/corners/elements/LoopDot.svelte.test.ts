import { render } from "vitest-browser-svelte";
import { describe, it, expect } from "vitest";
import LoopDot from "./LoopDot.svelte";

describe("LoopDot", () => {
	it("renders chart-2 dot for closed status", () => {
		const { container } = render(LoopDot, {
			value: { status: "closed" },
		});
		const el = container.querySelector("[data-testid='loop-dot']");
		expect(el).not.toBeNull();
		expect(el!.getAttribute("data-status")).toBe("closed");
		const style = el!.getAttribute("style") ?? "";
		expect(style).toContain("--chart-2");
		expect(el!.getAttribute("aria-label")).toBe("Loop closed");
	});

	it("renders muted dot when value is null", () => {
		const { container } = render(LoopDot, { value: null });
		const el = container.querySelector("[data-testid='loop-dot']");
		expect(el!.getAttribute("data-status")).toBe("no-data");
		const style = el!.getAttribute("style") ?? "";
		expect(style).toContain("--muted-foreground");
		expect(el!.getAttribute("aria-label")).toBe("Loop status no data");
	});

	it("renders destructive dot for open status", () => {
		const { container } = render(LoopDot, {
			value: { status: "open" },
		});
		const el = container.querySelector("[data-testid='loop-dot']");
		const style = el!.getAttribute("style") ?? "";
		expect(style).toContain("--destructive");
		expect(el!.getAttribute("aria-label")).toBe("Loop open");
	});

	it("renders chart-4 dot for limited status", () => {
		const { container } = render(LoopDot, {
			value: { status: "limited" },
		});
		const el = container.querySelector("[data-testid='loop-dot']");
		const style = el!.getAttribute("style") ?? "";
		expect(style).toContain("--chart-4");
		expect(el!.getAttribute("aria-label")).toBe("Loop limited");
	});
});
