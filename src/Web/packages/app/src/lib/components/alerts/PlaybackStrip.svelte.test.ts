import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import PlaybackStrip from "./PlaybackStrip.svelte";

const WINDOW_START = new Date("2025-01-01T00:00:00Z").getTime();
const WINDOW_END = new Date("2025-01-01T24:00:00Z").getTime();

function defaultProps() {
	return {
		playing: false,
		playPct: 50,
		maxPct: 60,
		currentDate: new Date(WINDOW_START + (WINDOW_END - WINDOW_START) / 2),
		speed: 1,
		events: [
			{ tMs: WINDOW_START + 1_000_000, severity: "warning", ruleId: "r1" },
			{ tMs: WINDOW_START + 5_000_000, severity: "critical", ruleId: "r2" },
			{ tMs: WINDOW_START + 9_000_000, severity: "info", ruleId: "r3" },
		],
		windowStartMs: WINDOW_START,
		windowEndMs: WINDOW_END,
		onPlayPause: () => {},
		onReset: () => {},
		onSeek: (_: number) => {},
	};
}

describe("PlaybackStrip", () => {
	it("renders one tick per event", async () => {
		render(PlaybackStrip, { props: defaultProps() });
		const ticks = document.querySelectorAll('[data-testid="event-tick"]');
		expect(ticks.length).toBe(3);
	});

	it("dims ticks past the playhead", async () => {
		// Push playhead to 0 → all ticks should be dimmed.
		render(PlaybackStrip, { props: { ...defaultProps(), playPct: 0 } });
		const ticks = Array.from(
			document.querySelectorAll<SVGLineElement>('[data-testid="event-tick"]'),
		);
		expect(ticks.length).toBe(3);
		for (const t of ticks) {
			expect(t.getAttribute("opacity")).toBe("0.35");
		}
	});

	it("calls onSeek when the strip is clicked", async () => {
		const onSeek = vi.fn();
		render(PlaybackStrip, { props: { ...defaultProps(), onSeek } });

		const strip = document.querySelector<SVGSVGElement>(
			'[data-testid="playback-tick-strip"]',
		);
		expect(strip).not.toBeNull();
		const rect = strip!.getBoundingClientRect();
		const ev = new PointerEvent("pointerdown", {
			clientX: rect.left + rect.width / 4,
			clientY: rect.top + rect.height / 2,
			bubbles: true,
		});
		strip!.dispatchEvent(ev);
		expect(onSeek).toHaveBeenCalledTimes(1);
		const arg = onSeek.mock.calls[0][0];
		expect(arg).toBeGreaterThanOrEqual(20);
		expect(arg).toBeLessThanOrEqual(30);
	});

	it("renders the play button label by playing state", async () => {
		render(PlaybackStrip, { props: { ...defaultProps(), playing: false } });
		await expect
			.element(page.getByLabelText("Play", { exact: true }))
			.toBeVisible();
	});

	it("renders the pause label while playing", async () => {
		render(PlaybackStrip, { props: { ...defaultProps(), playing: true } });
		await expect
			.element(page.getByLabelText("Pause", { exact: true }))
			.toBeVisible();
	});
});
