import { describe, expect, it } from "vitest";
import { getDirectionInfo } from "./index";

describe("getDirectionInfo", () => {
	it.each([
		["NOT COMPUTABLE", "unknown"],
		["NotComputable", "unknown"],
		["RATE OUT OF RANGE", "out of range"],
		["RateOutOfRange", "out of range"],
		["Flat", "stable"],
		["DoubleDown", "falling very fast"],
	])("resolves %s to %s", (direction, label) => {
		expect(getDirectionInfo(direction).label).toBe(label);
	});

	it("falls back to stable for unrecognised and absent values", () => {
		expect(getDirectionInfo("Bogus").label).toBe("stable");
		expect(getDirectionInfo(undefined).label).toBe("stable");
	});
});
