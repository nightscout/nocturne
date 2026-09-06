import { describe, it, expect } from "vitest";
import { deviceKindLabel } from "./device-kind-labels";

describe("deviceKindLabel", () => {
	it("maps known kinds to their display names", () => {
		expect(deviceKindLabel("companion")).toBe("Companion");
		expect(deviceKindLabel("prelude")).toBe("Prelude");
	});

	it("title-cases unknown kinds", () => {
		expect(deviceKindLabel("widget")).toBe("Widget");
	});

	it("falls back to the generic label when the kind is missing", () => {
		expect(deviceKindLabel(undefined)).toBe("Device");
		expect(deviceKindLabel("")).toBe("Device");
	});
});
