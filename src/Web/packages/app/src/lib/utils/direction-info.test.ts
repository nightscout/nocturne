import { describe, expect, it } from "vitest";
import { getDirectionInfo } from "./index";

describe("getDirectionInfo", () => {
	it.each([
		["NOT COMPUTABLE", "unknown"],
		["NotComputable", "unknown"],
		["None", "unknown"],
		["NONE", "unknown"],
		["RATE OUT OF RANGE", "out of range"],
		["RateOutOfRange", "out of range"],
		["Flat", "stable"],
		["DoubleDown", "falling very fast"],
	])("resolves %s to %s", (direction, label) => {
		expect(getDirectionInfo(direction).label).toBe(label);
	});

	it("falls back to unknown for unrecognised and absent values", () => {
		expect(getDirectionInfo("Bogus").label).toBe("unknown");
		expect(getDirectionInfo("").label).toBe("unknown");
		expect(getDirectionInfo(undefined).label).toBe("unknown");
	});

	it.each(["None", "NONE", "Bogus", "", undefined])(
		"does not render %s as the stable arrow",
		(direction) => {
			const info = getDirectionInfo(direction);
			expect(info.label).not.toBe("stable");
			expect(info.icon).not.toBe(getDirectionInfo("Flat").icon);
			expect(info.css).not.toBe(getDirectionInfo("Flat").css);
		},
	);
});
