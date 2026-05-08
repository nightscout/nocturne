import { render } from "vitest-browser-svelte";
import { describe, it, expect, vi, afterEach } from "vitest";
import TrendChevron from "./TrendChevron.svelte";
import TrendChevronHarness from "./TrendChevronHarness.test.svelte";

describe("TrendChevron", () => {
	it("renders nothing when stale", async () => {
		const { container } = render(TrendChevronHarness, {
			delta: 0,
			color: "#fff",
			stale: true,
		});
		const g = container.querySelector("g");
		expect(g).toBeNull();
	});

	it("renders a rotated <g> when not stale (delta=0 → rotate(0))", async () => {
		const { container } = render(TrendChevronHarness, {
			delta: 0,
			color: "#abcdef",
			stale: false,
		});
		const g = container.querySelector("g");
		expect(g).not.toBeNull();
		const transform = g?.getAttribute("transform") ?? "";
		expect(transform).toContain("rotate(0");
	});

	it("renders a path with the provided fill color", async () => {
		const { container } = render(TrendChevronHarness, {
			delta: 0,
			color: "#abcdef",
			stale: false,
		});
		const path = container.querySelector("path");
		expect(path).not.toBeNull();
		expect(path?.getAttribute("fill")).toBe("#abcdef");
	});

	it("module export is the component", () => {
		expect(TrendChevron).toBeTruthy();
	});

	describe("tween settling", () => {
		afterEach(() => {
			vi.useRealTimers();
		});

		it("settles to the eased angle for delta=5 after the tween completes", async () => {
			vi.useFakeTimers({ shouldAdvanceTime: true });
			const { container } = render(TrendChevronHarness, {
				delta: 5,
				color: "red",
				stale: false,
			});
			await vi.advanceTimersByTimeAsync(700);
			const g = container.querySelector("g");
			expect(g).not.toBeNull();
			const transform = g?.getAttribute("transform") ?? "";
			expect(transform).toContain("rotate(-30 ");
		});
	});
});
