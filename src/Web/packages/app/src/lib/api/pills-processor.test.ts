import { describe, it, expect, vi } from "vitest";

// The module graph for $lib/api transitively imports several $app/* modules
// and @sveltejs/kit.  Mock them all before loading the module under test.
vi.mock("$app/environment", () => ({ browser: false, dev: false }));
vi.mock("$app/navigation", () => ({}));
vi.mock("$app/state", () => ({}));
vi.mock("$app/server", () => ({
	getRequestEvent: vi.fn(),
	query: (fn: any) => fn,
	command: (fn: any) => fn,
	form: (fn: any) => fn,
}));
vi.mock("@sveltejs/kit", () => ({
	error: vi.fn((status: number, msg: string) => { throw Object.assign(new Error(msg), { status }); }),
	redirect: vi.fn((status: number, url: string) => { throw Object.assign(new Error(url), { status }); }),
}));

const { processPillsData } = await import("$api/pills-processor");

// ---------------------------------------------------------------------------
// Test helpers
// ---------------------------------------------------------------------------

const MIN = 60_000;

/** Build a minimal ApsSnapshot. mills defaults to `now` (injected via config). */
function makeSnapshot(
	overrides: Record<string, unknown> = {},
	now: number,
	ageMins = 0
) {
	return {
		id: crypto.randomUUID(),
		mills: now - ageMins * MIN,
		aidAlgorithm: "Trio",
		iob: 2.5,
		basalIob: 1.2,
		cob: 15,
		enacted: false,
		enactedRate: undefined,
		enactedDuration: undefined,
		enactedBolusVolume: undefined,
		eventualBg: 120,
		...overrides,
	} as any;
}

const EMPTY: any[] = [];
const NOW = 1_700_000_000_000; // fixed reference timestamp

// ---------------------------------------------------------------------------
// IOB
// ---------------------------------------------------------------------------

describe("processPillsData – IOB from APS snapshot", () => {
	it("returns IOB value from a recent snapshot", () => {
		const snap = makeSnapshot({ iob: 2.72 }, NOW, 3);
		const result = processPillsData(EMPTY, [snap], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.iob).not.toBeNull();
		expect(result.iob!.iob).toBe(2.72);
		expect(result.iob!.display).toBe("2.72U");
	});

	it("includes basalIob when present", () => {
		const snap = makeSnapshot({ iob: 2.72, basalIob: 1.5 }, NOW, 3);
		const result = processPillsData(EMPTY, [snap], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.iob!.basalIob).toBe(1.5);
	});

	it("labels Trio correctly", () => {
		const snap = makeSnapshot({ aidAlgorithm: "Trio" }, NOW, 3);
		const result = processPillsData(EMPTY, [snap], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.iob!.source).toBe("Trio");
	});

	it("labels Loop correctly", () => {
		const snap = makeSnapshot({ aidAlgorithm: "Loop" }, NOW, 3);
		const result = processPillsData(EMPTY, [snap], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.iob!.source).toBe("Loop");
	});

	it("labels OpenAps correctly", () => {
		const snap = makeSnapshot({ aidAlgorithm: "OpenAps" }, NOW, 3);
		const result = processPillsData(EMPTY, [snap], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.iob!.source).toBe("OpenAPS");
	});

	it("labels AndroidAps correctly", () => {
		const snap = makeSnapshot({ aidAlgorithm: "AndroidAps" }, NOW, 3);
		const result = processPillsData(EMPTY, [snap], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.iob!.source).toBe("AAPS");
	});

	it("ignores snapshots older than 30 minutes", () => {
		const stale = makeSnapshot({ iob: 2.72 }, NOW, 35);
		const result = processPillsData(EMPTY, [stale], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.iob).toBeNull();
	});

	it("returns null when no snapshots are provided", () => {
		const result = processPillsData(EMPTY, EMPTY, EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.iob).toBeNull();
	});

	it("skips snapshots where iob is undefined, uses next", () => {
		const old = makeSnapshot({ iob: 1.0 }, NOW, 10);
		const recent = makeSnapshot({ iob: 2.72 }, NOW, 3);
		// Most recent is preferred
		const result = processPillsData(EMPTY, [old, recent], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.iob!.iob).toBe(2.72);
	});

	it("picks the most recent of multiple valid snapshots", () => {
		const older = makeSnapshot({ iob: 1.0 }, NOW, 20);
		const newer = makeSnapshot({ iob: 2.72 }, NOW, 5);
		const result = processPillsData(EMPTY, [older, newer], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.iob!.iob).toBe(2.72);
	});

	it("prefers snapshot over legacy DeviceStatus", () => {
		const snap = makeSnapshot({ iob: 2.72 }, NOW, 3);
		const legacyStatus = [{
			mills: NOW - 2 * MIN,
			loop: { iob: { iob: 9.99, timestamp: new Date(NOW - 2 * MIN).toISOString() } }
		}] as any[];
		const result = processPillsData(legacyStatus, [snap], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.iob!.iob).toBe(2.72);
	});
});

// ---------------------------------------------------------------------------
// COB
// ---------------------------------------------------------------------------

describe("processPillsData – COB from APS snapshot", () => {
	it("returns COB value from a recent snapshot", () => {
		const snap = makeSnapshot({ cob: 15 }, NOW, 3);
		const result = processPillsData(EMPTY, [snap], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.cob).not.toBeNull();
		expect(result.cob!.cob).toBe(15);
		expect(result.cob!.display).toBe("15g");
	});

	it("returns COB = 0 as valid (not null)", () => {
		const snap = makeSnapshot({ cob: 0 }, NOW, 3);
		const result = processPillsData(EMPTY, [snap], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.cob).not.toBeNull();
		expect(result.cob!.cob).toBe(0);
		expect(result.cob!.display).toBe("0g");
	});

	it("ignores snapshots where cob is undefined", () => {
		const snap = makeSnapshot({ cob: undefined }, NOW, 3);
		const result = processPillsData(EMPTY, [snap], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		// Falls through to legacy path which also returns null with no carbs
		expect(result.cob).toBeNull();
	});

	it("ignores stale COB snapshots", () => {
		const stale = makeSnapshot({ cob: 15 }, NOW, 35);
		const result = processPillsData(EMPTY, [stale], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.cob).toBeNull();
	});
});

// ---------------------------------------------------------------------------
// Loop
// ---------------------------------------------------------------------------

describe("processPillsData – Loop from APS snapshot", () => {
	it("returns looping status for a recent snapshot", () => {
		const snap = makeSnapshot({ enacted: false }, NOW, 3);
		const result = processPillsData(EMPTY, [snap], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.loop).not.toBeNull();
		expect(result.loop!.status).toBe("looping");
		expect(result.loop!.symbol).toBe("↻");
		expect(result.loop!.level).toBe("none");
	});

	it("returns display in minutes for a recent snapshot", () => {
		const snap = makeSnapshot({}, NOW, 5);
		const result = processPillsData(EMPTY, [snap], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.loop!.display).toBe("5m");
	});

	it("returns warning status at 30-59 minutes", () => {
		const snap = makeSnapshot({}, NOW, 40);
		const result = processPillsData(EMPTY, [snap], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.loop!.status).toBe("warning");
		expect(result.loop!.symbol).toBe("⚠");
		expect(result.loop!.level).toBe("warn");
	});

	it("returns urgent level at 60+ minutes", () => {
		const snap = makeSnapshot({}, NOW, 75);
		const result = processPillsData(EMPTY, [snap], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.loop!.status).toBe("warning");
		expect(result.loop!.symbol).toBe("⚠");
		expect(result.loop!.level).toBe("urgent");
	});

	it("returns enacted status for a recent enacted snapshot", () => {
		const snap = makeSnapshot({ enacted: true, enactedRate: 0.5, enactedDuration: 30 }, NOW, 5);
		const result = processPillsData(EMPTY, [snap], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.loop!.status).toBe("enacted");
		expect(result.loop!.symbol).toBe("⌁");
	});

	it("does not return enacted status for an old enacted snapshot (>15 min)", () => {
		const snap = makeSnapshot({ enacted: true, enactedRate: 0.5, enactedDuration: 30 }, NOW, 20);
		const result = processPillsData(EMPTY, [snap], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		// 20 min is within warn threshold (30 min) so status is looping, not enacted
		expect(result.loop!.status).toBe("looping");
	});

	it("returns null when no snapshots are within 6 hours", () => {
		const snap = makeSnapshot({}, NOW, 7 * 60); // 7 hours ago
		const result = processPillsData(EMPTY, [snap], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.loop).toBeNull();
	});

	it("populates lastLoopTime from snapshot mills", () => {
		const mills = NOW - 5 * MIN;
		const snap = { ...makeSnapshot({}, NOW, 0), mills };
		const result = processPillsData(EMPTY, [snap], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.loop!.lastLoopTime).toBe(mills);
	});

	it("exposes IOB and COB on the loop pill", () => {
		const snap = makeSnapshot({ iob: 2.72, cob: 10 }, NOW, 3);
		const result = processPillsData(EMPTY, [snap], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.loop!.iob).toBe(2.72);
		expect(result.loop!.cob).toBe(10);
	});

	it("exposes eventualBG on the loop pill", () => {
		const snap = makeSnapshot({ eventualBg: 130 }, NOW, 3);
		const result = processPillsData(EMPTY, [snap], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.loop!.eventualBG).toBe(130);
	});

	it("exposes lastEnacted details when enacted", () => {
		const snap = makeSnapshot({ enacted: true, enactedRate: 0.75, enactedDuration: 30 }, NOW, 5);
		const result = processPillsData(EMPTY, [snap], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.loop!.lastEnacted).toBeDefined();
		expect(result.loop!.lastEnacted!.rate).toBe(0.75);
		expect(result.loop!.lastEnacted!.type).toBe("temp_basal");
	});

	it("sets lastEnacted type to cancel when enactedRate is 0", () => {
		const snap = makeSnapshot({ enacted: true, enactedRate: 0, enactedDuration: 30 }, NOW, 5);
		const result = processPillsData(EMPTY, [snap], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.loop!.lastEnacted!.type).toBe("cancel");
	});

	it("sets lastEnacted type to bolus when enactedBolusVolume is set", () => {
		const snap = makeSnapshot({ enacted: true, enactedBolusVolume: 0.3, enactedRate: 0 }, NOW, 5);
		const result = processPillsData(EMPTY, [snap], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.loop!.lastEnacted!.type).toBe("bolus");
	});

	it("uses the most recent of multiple snapshots", () => {
		const old = makeSnapshot({ iob: 1.0 }, NOW, 45); // warn range
		const recent = makeSnapshot({ iob: 2.72 }, NOW, 5); // looping
		const result = processPillsData(EMPTY, [old, recent], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.loop!.status).toBe("looping");
	});

	it("prefers snapshot loop over legacy DeviceStatus loop", () => {
		const snap = makeSnapshot({ iob: 2.72 }, NOW, 3);
		const legacyStatus = [{
			mills: NOW - 2 * MIN,
			openaps: {
				iob: { iob: 9.99 },
				enacted: { rate: 0, duration: 0, timestamp: new Date(NOW - 2 * MIN).toISOString(), received: true }
			}
		}] as any[];
		const result = processPillsData(legacyStatus, [snap], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.loop!.iob).toBe(2.72);
	});
});

// ---------------------------------------------------------------------------
// Basal
// ---------------------------------------------------------------------------

describe("processPillsData – Basal from APS snapshot", () => {
	it("shows an active temp basal when enactedDuration has not expired", () => {
		// Snapshot 5 min ago, enacted a 30-min temp basal → 25 min remaining
		const snap = makeSnapshot(
			{ enacted: true, enactedRate: 1.2, enactedDuration: 30 },
			NOW, 5
		);
		const result = processPillsData(EMPTY, [snap], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.basal).not.toBeNull();
		expect(result.basal!.isTempBasal).toBe(true);
		expect(result.basal!.totalBasal).toBe(1.2);
		expect(result.basal!.tempBasal).toBeDefined();
		expect(result.basal!.tempBasal!.remaining).toBeCloseTo(25, 0);
	});

	it("ignores an enacted snapshot whose temp basal has already expired", () => {
		// Snapshot 35 min ago, enacted a 30-min temp basal → expired 5 min ago
		const snap = makeSnapshot(
			{ enacted: true, enactedRate: 1.2, enactedDuration: 30 },
			NOW, 35
		);
		const result = processPillsData(EMPTY, [snap], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		// Snapshot function returns null (expired), falls through to processBasal with empty deviceStatuses
		// processBasal with no profile and no deviceStatuses returns null
		expect(result.basal).toBeNull();
	});

	it("returns null when no enacted snapshot is active", () => {
		const snap = makeSnapshot({ enacted: false }, NOW, 3);
		const result = processPillsData(EMPTY, [snap], EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.basal).toBeNull();
	});
});

// ---------------------------------------------------------------------------
// Fallback: empty snapshots fall through to legacy DeviceStatus path
// ---------------------------------------------------------------------------

describe("processPillsData – legacy DeviceStatus fallback when no snapshots", () => {
	it("uses legacy Loop when no snapshots are provided", () => {
		const legacyStatus = [{
			mills: NOW - 3 * MIN,
			loop: {
				timestamp: new Date(NOW - 3 * MIN).toISOString(),
				iob: { iob: 1.5, timestamp: new Date(NOW - 3 * MIN).toISOString() },
				cob: { cob: 8, timestamp: new Date(NOW - 3 * MIN).toISOString() },
			}
		}] as any[];
		const result = processPillsData(legacyStatus, EMPTY, EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.loop).not.toBeNull();
		expect(result.loop!.loopName).toBe("Loop");
	});

	it("uses legacy IOB when no snapshots are provided", () => {
		const legacyStatus = [{
			mills: NOW - 3 * MIN,
			openaps: {
				iob: { iob: 1.5, timestamp: new Date(NOW - 3 * MIN).toISOString() }
			}
		}] as any[];
		const result = processPillsData(legacyStatus, EMPTY, EMPTY, EMPTY, EMPTY, null, { now: NOW });
		expect(result.iob).not.toBeNull();
		expect(result.iob!.iob).toBe(1.5);
	});
});