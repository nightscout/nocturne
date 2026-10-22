import { render } from "vitest-browser-svelte";
import { describe, it, expect, vi } from "vitest";
import DayStackedBar from "./DayStackedBar.svelte";

const props = {
	veryLowPercent: 5,
	lowPercent: 10,
	inRangePercent: 50,
	highPercent: 20,
	veryHighPercent: 15,
};

describe("DayStackedBar", () => {
	it("stacks five segments, very low at the bottom to very high at the top", async () => {
		const { container } = render(DayStackedBar, { ...props, onclick: vi.fn() });
		const host = container.firstElementChild;
		if (!(host instanceof HTMLElement)) throw new Error("no host element");
		host.style.height = "100px";

		await vi.waitFor(() => {
			const heights = [...container.querySelectorAll(".lc-bars-bar")].map(
				(bar) => bar.getBoundingClientRect().height,
			);
			expect(heights).toHaveLength(5);
			expect(heights.every((height) => height > 0)).toBe(true);
		});

		const fillsTopToBottom = [...container.querySelectorAll(".lc-bars-bar")]
			.map((bar) => ({
				fill: bar.getAttribute("fill"),
				top: bar.getBoundingClientRect().top,
			}))
			.sort((a, b) => a.top - b.top)
			.map((bar) => bar.fill);

		expect(fillsTopToBottom).toEqual([
			"var(--glucose-very-high)",
			"var(--glucose-high)",
			"var(--glucose-in-range)",
			"var(--glucose-low)",
			"var(--glucose-very-low)",
		]);
	});
});
