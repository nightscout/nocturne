import { describe, it, expect } from "vitest";
import { toIsoString } from "./api-date";

describe("toIsoString", () => {
	it("normalises the ISO string the client actually returns for a `Date` field", () => {
		expect(toIsoString("2026-08-14T03:00:00Z")).toBe("2026-08-14T03:00:00.000Z");
	});

	it("accepts a real Date, in case a field is ever hydrated", () => {
		expect(toIsoString(new Date(Date.UTC(2026, 7, 14, 3)))).toBe("2026-08-14T03:00:00.000Z");
	});

	it("returns null for absent or unparseable values", () => {
		expect(toIsoString(null)).toBeNull();
		expect(toIsoString(undefined)).toBeNull();
		expect(toIsoString("")).toBeNull();
		expect(toIsoString("not a date")).toBeNull();
	});
});
