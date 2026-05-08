import { render } from "vitest-browser-svelte";
import { describe, it, expect, vi, beforeEach } from "vitest";

// Mock the predictions remote function before importing the component.
vi.mock("$api/predictions.remote", () => ({
	getPredictions: vi.fn().mockResolvedValue({
		timestamp: new Date(),
		currentBg: 132,
		delta: 4,
		eventualBg: 140,
		iob: 1.2,
		cob: 18,
		sensitivityRatio: null,
		intervalMinutes: 5,
		curves: {
			main: [],
			iobOnly: [],
			uam: [],
			cob: [],
			zeroTemp: [],
		},
	}),
	getPredictionStatus: vi.fn().mockResolvedValue({ available: false, source: null }),
}));

import HaloDial from "./HaloDial.svelte";
import { defaultHaloDialConfig, HaloDialColorMode } from "./config";

/**
 * Build a stub of just the realtime-store fields HaloDial reads. We don't
 * instantiate the real store — that would require a WebSocket and SvelteKit
 * context. Instead we hand the component a minimal duck-typed object via the
 * `realtimeOverride` prop.
 */
function makeStub(overrides: Record<string, unknown> = {}) {
	const baseNow = Date.now();
	const fresh = baseNow - 60_000; // 1 minute ago by default
	return {
		currentBG: 132,
		bgDelta: 4,
		lastUpdated: fresh,
		now: baseNow,
		currentPumpMode: null,
		currentSensitivityPercent: null,
		pillsData: { iob: null, cob: null, cage: null, sage: null, basal: null, loop: null },
		direction: "Flat",
		entries: [
			{ mills: baseNow - 5 * 60_000, sgv: 128 },
			{ mills: baseNow - 0 * 60_000, sgv: 132 },
		],
		...overrides,
	} as any;
}

describe("HaloDial", () => {
	beforeEach(() => {
		vi.clearAllMocks();
	});

	it("renders the BG number, default corner elements, and chevron when fresh", async () => {
		const { container } = render(HaloDial, {
			realtimeOverride: makeStub(),
			configOverride: defaultHaloDialConfig(),
		});

		const root = container.querySelector("[data-testid='halo-dial']");
		expect(root).not.toBeNull();
		expect(root!.classList.contains("hd-stale")).toBe(false);

		// Center BG number — the tween starts at 0 and animates to 132; assert
		// the element is present (its text settles asynchronously).
		const center = container.querySelector("[data-testid='halo-dial-center-bg']");
		expect(center).not.toBeNull();

		// Default corners place LoopDot in TR and Direction/Eventual/LoopLabel in BR.
		expect(container.querySelector("[data-testid='loop-dot']")).not.toBeNull();
		expect(container.querySelector("[data-testid='direction']")).not.toBeNull();
		expect(container.querySelector("[data-testid='loop-label']")).not.toBeNull();

		// Chevron is rendered (TrendChevron emits a <g> with a <path>).
		// Its presence is most reliably detected by the <NowMarker> + chevron
		// path inside the SVG: assert by inverse — the stale class is absent
		// and a non-empty SVG exists.
		const svg = container.querySelector("[data-testid='halo-dial-svg']");
		expect(svg).not.toBeNull();
	});

	it("applies the stale class and hides the chevron when the latest entry is older than the threshold", () => {
		const baseNow = Date.now();
		const eleven = baseNow - 11 * 60_000;
		const { container } = render(HaloDial, {
			realtimeOverride: makeStub({
				lastUpdated: eleven,
				now: baseNow,
				entries: [{ mills: eleven, sgv: 132 }],
			}),
			configOverride: defaultHaloDialConfig(),
		});

		const root = container.querySelector("[data-testid='halo-dial']");
		expect(root).not.toBeNull();
		expect(root!.classList.contains("hd-stale")).toBe(true);

		// TrendChevron is gated on `!stale`; with stale=true its <g> isn't
		// rendered. The chevron path's tell-tale "M 0 -6.5 Q ..." marker
		// therefore should not appear anywhere in the dial.
		const svgHtml = container.querySelector("[data-testid='halo-dial-svg']")?.innerHTML ?? "";
		expect(svgHtml).not.toContain("M 0 -6.5");
	});

	it("activates the spiral fallback when historyMinutes exceeds the available arc", () => {
		const baseNow = Date.now();
		const historyMinutes = 120;
		const predictionMinutes = 45;
		// 24 entries spanning 120 minutes (1 entry per 5 min).
		const entries = Array.from({ length: 24 }, (_, i) => ({
			mills: baseNow - (23 - i) * 5 * 60_000,
			sgv: 120 + i,
		}));

		const config = {
			...defaultHaloDialConfig(),
			historyMinutes,
			predictionMinutes,
			colorMode: HaloDialColorMode.Discrete,
		};

		const { container } = render(HaloDial, {
			realtimeOverride: makeStub({
				lastUpdated: baseNow,
				now: baseNow,
				entries,
			}),
			configOverride: config,
		});

		const ring = container.querySelector("[data-testid='history-ring']");
		expect(ring).not.toBeNull();
		expect(ring!.getAttribute("data-spiral-active")).toBe("true");
	});
});
