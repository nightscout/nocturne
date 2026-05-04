import { describe, it, expect } from "vitest";
import { directions, getDirectionInfo } from "./serverSettings";

describe("directions", () => {
	it("maps known directions to labels", () => {
		expect(directions.Flat.label).toBe("→");
		expect(directions.SingleUp.label).toBe("↑");
		expect(directions.SingleDown.label).toBe("↓");
		expect(directions.DoubleUp.label).toBe("⇈");
		expect(directions.DoubleDown.label).toBe("⇊");
		expect(directions.FortyFiveUp.label).toBe("↗");
		expect(directions.FortyFiveDown.label).toBe("↘");
	});

	it("maps known directions to descriptions", () => {
		expect(directions.Flat.description).toBe("Stable");
		expect(directions.SingleUp.description).toBe("Rising");
		expect(directions.SingleDown.description).toBe("Falling");
		expect(directions.DoubleUp.description).toBe("Rising quickly");
		expect(directions.DoubleDown.description).toBe("Falling quickly");
	});

	it("includes NOT COMPUTABLE direction", () => {
		expect(directions["NOT COMPUTABLE"].label).toBe("-");
		expect(directions["NOT COMPUTABLE"].description).toBe("Not computable");
	});

	it("includes RATE OUT OF RANGE direction", () => {
		expect(directions["RATE OUT OF RANGE"].label).toBe("⇕");
	});
});

describe("getDirectionInfo", () => {
	it("returns correct info for known directions", () => {
		expect(getDirectionInfo("Flat")).toEqual({
			label: "→",
			description: "Stable",
		});

		expect(getDirectionInfo("SingleUp")).toEqual({
			label: "↑",
			description: "Rising",
		});
	});

	it("returns NOT COMPUTABLE for unknown directions", () => {
		expect(getDirectionInfo("InvalidDirection")).toEqual({
			label: "-",
			description: "Not computable",
		});
	});

	it("returns NOT COMPUTABLE for empty string", () => {
		expect(getDirectionInfo("")).toEqual({
			label: "-",
			description: "Not computable",
		});
	});

	it("returns NONE direction correctly", () => {
		expect(getDirectionInfo("NONE")).toEqual({
			label: "→",
			description: "No direction",
		});
	});
});
