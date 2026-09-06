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
		["CGM ERROR", "sensor error"],
		["CgmError", "sensor error"],
		["Flat", "stable"],
		["FORTY_FIVE_UP", "rising slowly"],
		["DoubleDown", "falling very fast"],
		["TripleUp", "rising extremely fast"],
		["TripleDown", "falling extremely fast"],
		["TRIPLE_UP", "rising extremely fast"],
	])("resolves %s to %s", (direction, label) => {
		expect(getDirectionInfo(direction).label).toBe(label);
	});

	it("does not report the fastest trends as unknown", () => {
		for (const direction of ["TripleUp", "TripleDown"]) {
			expect(getDirectionInfo(direction).label).not.toBe("unknown");
			expect(getDirectionInfo(direction).icon).not.toBe(
				getDirectionInfo("Bogus").icon,
			);
		}
	});

	it("falls back to unknown for unrecognised and absent values", () => {
		expect(getDirectionInfo("Bogus").label).toBe("unknown");
		expect(getDirectionInfo("").label).toBe("unknown");
		expect(getDirectionInfo(undefined).label).toBe("unknown");
	});

	it.each([
		"None",
		"NONE",
		"NotComputable",
		"RateOutOfRange",
		"CgmError",
		"Bogus",
		"",
		undefined,
	])(
		"does not render %s as the stable arrow",
		(direction) => {
			const info = getDirectionInfo(direction);
			expect(info.label).not.toBe("stable");
			expect(info.icon).not.toBe(getDirectionInfo("Flat").icon);
			expect(info.css).not.toBe(getDirectionInfo("Flat").css);
		},
	);
});
