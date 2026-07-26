import { describe, it, expect } from "vitest";
import { localDayStart, localDayEnd } from "./timezone";

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
