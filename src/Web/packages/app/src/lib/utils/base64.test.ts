import { describe, it, expect } from "vitest";
import { encodeBase64Utf8, decodeBase64Utf8 } from "./index";

describe("encodeBase64Utf8", () => {
	it("round-trips text btoa cannot encode at all", () => {
		for (const value of ["インスリン", "🩸 test strips", "тест"]) {
			expect(() => btoa(value)).toThrow();
			expect(decodeBase64Utf8(encodeBase64Utf8(value))).toBe(value);
		}
	});

	it("round-trips Latin-1 accents", () => {
		for (const value of ["Nålar", "Süßstoff", "aiguilles à insuline"]) {
			expect(decodeBase64Utf8(encodeBase64Utf8(value))).toBe(value);
		}
	});

	it("round-trips a JSON payload of user-named items", () => {
		const items = [{ c: "Förbrukning", l: "Nålar", q: 3 }];
		expect(
			JSON.parse(decodeBase64Utf8(encodeBase64Utf8(JSON.stringify(items))))
		).toEqual(items);
	});
});

describe("decodeBase64Utf8", () => {
	it("decodes ASCII payloads written by plain btoa, so existing links keep working", () => {
		const json = JSON.stringify([{ c: "Pump", l: "Reservoirs", q: 5 }]);
		expect(decodeBase64Utf8(btoa(json))).toBe(json);
	});

	it("falls back to the Latin-1 reading when a btoa payload isn't valid UTF-8", () => {
		const json = JSON.stringify([{ c: "Förbrukning", l: "Nålar", q: 3 }]);
		expect(decodeBase64Utf8(btoa(json))).toBe(json);
	});
});
