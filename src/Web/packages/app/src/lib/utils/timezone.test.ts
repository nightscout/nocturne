import { describe, it, expect } from "vitest";
import { localDayStart, localDayEnd, getLocalDayBoundariesUtc } from "./timezone";

describe("localDayStart", () => {
	it("returns local midnight, not UTC midnight", () => {
		const start = localDayStart("2026-03-15");
		expect(start.getFullYear()).toBe(2026);
		expect(start.getMonth()).toBe(2);
		expect(start.getDate()).toBe(15);
		expect(start.getHours()).toBe(0);
		expect(start.getMinutes()).toBe(0);
	});
});

describe("localDayEnd", () => {
	it("returns the last millisecond of the local day", () => {
		const end = localDayEnd("2026-03-15");
		expect(end.getDate()).toBe(15);
		expect(end.getHours()).toBe(23);
		expect(end.getMinutes()).toBe(59);
		expect(end.getSeconds()).toBe(59);
		expect(end.getMilliseconds()).toBe(999);
	});

	it("stays inside the requested day, whatever its length", () => {
		// A fixed +24h from local midnight overshoots a 23-hour spring-forward day
		// and falls an hour short of a 25-hour fall-back one.
		for (const date of ["2026-03-08", "2026-11-01", "2026-03-29", "2026-10-25"]) {
			const start = localDayStart(date);
			const end = localDayEnd(date);
			expect(end.getDate()).toBe(start.getDate());
			expect(end.getTime()).toBeGreaterThan(start.getTime());

			const nextDay = new Date(end.getTime() + 1);
			expect(nextDay.getDate()).not.toBe(start.getDate());
			expect(nextDay.getHours()).toBe(0);
		}
	});
});

describe("getLocalDayBoundariesUtc", () => {
	/** First instant of a local calendar day, found by brute-force Intl scan. */
	function firstInstantOfDay(dateStr: string, timeZone: string): number {
		const utcNoon = Date.parse(dateStr + "T12:00:00Z");
		for (let t = utcNoon - 24 * 60 * 60 * 1000; t <= utcNoon; t += 60 * 1000) {
			if (new Intl.DateTimeFormat("en-CA", { timeZone }).format(new Date(t)) === dateStr) return t;
		}
		throw new Error(`no instant found for ${dateStr} in ${timeZone}`);
	}

	function expectDayBoundaries(dateStr: string, timeZone: string) {
		const { start, end } = getLocalDayBoundariesUtc(dateStr, timeZone);
		expect(start.getTime()).toBe(firstInstantOfDay(dateStr, timeZone));

		const [y, m, d] = dateStr.split("-").map(Number);
		const nextDateStr = new Date(Date.UTC(y, m - 1, d + 1)).toISOString().slice(0, 10);
		const nextStart = getLocalDayBoundariesUtc(nextDateStr, timeZone).start;
		expect(end.getTime()).toBe(nextStart.getTime() - 1);

		// The whole window must display as the requested local date.
		for (const probe of [start, end]) {
			expect(
				new Intl.DateTimeFormat("en-CA", { timeZone }).format(probe)
			).toBe(dateStr);
		}
	}

	it("handles an ordinary day", () => {
		expectDayBoundaries("2026-06-15", "Europe/Berlin");
	});

	it("starts at the first instant of the local day when DST begins at midnight (Chile)", () => {
		// America/Santiago: clocks jump 2026-09-06 00:00 -> 01:00 (-04 -> -03),
		// i.e. the local day begins at 2026-09-06T04:00:00Z. The buggy
		// string-roundtrip computed the offset from the *previous* day's
		// instant and produced the same 04:00Z only by accident on the start,
		// but derived end as start+24h, overlapping into the next day.
		const { start } = getLocalDayBoundariesUtc("2026-09-06", "America/Santiago");
		expect(start.toISOString()).toBe("2026-09-06T04:00:00.000Z");
		// Sanity via Intl: that instant is the earliest one displaying as Sep 6.
		expect(new Intl.DateTimeFormat("en-US", { timeZone: "America/Santiago" })
			.format(new Date(start.getTime() - 1))).toMatch(/9\/5/);
		expectDayBoundaries("2026-09-06", "America/Santiago");
	});

	it("yields a 23-hour day on the spring-forward date and a 25-hour day on the fall-back date", () => {
		const spring = getLocalDayBoundariesUtc("2026-09-06", "America/Santiago");
		expect(spring.end.getTime() - spring.start.getTime() + 1).toBe(23 * 60 * 60 * 1000);

		// Chile falls back on 2027-04-03 at 00:00 -> 23:00 (-03 -> -04); the extra
		// hour repeats at the END of Apr 3, so the 25-hour day is Apr 3.
		const fall = getLocalDayBoundariesUtc("2027-04-03", "America/Santiago");
		expect(fall.end.getTime() - fall.start.getTime() + 1).toBe(25 * 60 * 60 * 1000);
		expect(
			getLocalDayBoundariesUtc("2027-04-04", "America/Santiago").end.getTime() -
				getLocalDayBoundariesUtc("2027-04-04", "America/Santiago").start.getTime() +
				1
		).toBe(24 * 60 * 60 * 1000);
	});

	it("keeps boundaries inside the requested local day across European DST changes", () => {
		// Spring forward / fall back in Berlin (transitions at 02:00/03:00 local,
		// not midnight — but the end must still land before the next midnight).
		for (const date of ["2026-03-29", "2026-10-25"]) {
			expectDayBoundaries(date, "Europe/Berlin");
		}
	});
});
