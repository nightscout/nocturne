import { describe, it, expect, beforeEach, vi, afterEach } from "vitest";
import { TitleFaviconService, type GlucoseStatus } from "./title-favicon-service.svelte";
import type { ClientThresholds, TitleFaviconSettings } from "$lib/stores/serverSettings";

// Mock $app/environment
vi.mock("$app/environment", () => ({ browser: true }));

// Mock formatting utils - they depend on global glucose units
vi.mock("$lib/utils/formatting", () => ({
	bg: (mgdl: number) => Math.round(mgdl),
	bgDelta: (delta: number) => (delta > 0 ? `+${Math.round(delta)}` : `${Math.round(delta)}`),
}));

const default_thresholds: ClientThresholds = {
	high: 260,
	targetTop: 180,
	targetBottom: 70,
	low: 55,
};

function make_settings(overrides: Partial<TitleFaviconSettings> = {}): TitleFaviconSettings {
	return {
		enabled: true,
		showBgValue: true,
		showDirection: true,
		showDelta: true,
		customPrefix: "",
		faviconEnabled: false,
		faviconShowBg: false,
		faviconColorCoded: false,
		flashOnAlarm: false,
		...overrides,
	};
}

describe("TitleFaviconService", () => {
	let service: TitleFaviconService;

	beforeEach(() => {
		service = new TitleFaviconService();
	});

	afterEach(() => {
		service.destroy();
	});

	describe("getGlucoseStatus", () => {
		it("returns 'very-high' when value >= high threshold", () => {
			expect(service.getGlucoseStatus(260, default_thresholds)).toBe("very-high");
			expect(service.getGlucoseStatus(300, default_thresholds)).toBe("very-high");
		});

		it("returns 'high' when value > targetTop but < high", () => {
			expect(service.getGlucoseStatus(200, default_thresholds)).toBe("high");
			expect(service.getGlucoseStatus(181, default_thresholds)).toBe("high");
		});

		it("returns 'in-range' when value between targetBottom and targetTop", () => {
			expect(service.getGlucoseStatus(120, default_thresholds)).toBe("in-range");
			expect(service.getGlucoseStatus(70, default_thresholds)).toBe("in-range");
			expect(service.getGlucoseStatus(180, default_thresholds)).toBe("in-range");
		});

		it("returns 'low' when value < targetBottom but > low", () => {
			expect(service.getGlucoseStatus(60, default_thresholds)).toBe("low");
			expect(service.getGlucoseStatus(69, default_thresholds)).toBe("low");
		});

		it("returns 'very-low' when value <= low threshold", () => {
			expect(service.getGlucoseStatus(55, default_thresholds)).toBe("very-low");
			expect(service.getGlucoseStatus(40, default_thresholds)).toBe("very-low");
		});

		it("handles boundary values correctly", () => {
			// At exactly targetTop → in-range (<=)
			expect(service.getGlucoseStatus(180, default_thresholds)).toBe("in-range");
			// At exactly targetBottom → in-range (>=)
			expect(service.getGlucoseStatus(70, default_thresholds)).toBe("in-range");
			// At exactly high → very-high
			expect(service.getGlucoseStatus(260, default_thresholds)).toBe("very-high");
			// At exactly low → very-low
			expect(service.getGlucoseStatus(55, default_thresholds)).toBe("very-low");
		});
	});

	describe("isFlashing", () => {
		it("is false by default", () => {
			expect(service.isFlashing).toBe(false);
		});
	});

	describe("initialize / destroy lifecycle", () => {
		it("can be destroyed without initialization", () => {
			// Should not throw
			service.destroy();
			expect(service.isFlashing).toBe(false);
		});

		it("can be destroyed multiple times safely", () => {
			service.destroy();
			service.destroy();
			expect(service.isFlashing).toBe(false);
		});
	});

	describe("update with disabled settings", () => {
		it("does not throw when not initialized", () => {
			const settings = make_settings({ enabled: true });
			// Should not throw even without initialize()
			expect(() =>
				service.update(120, "Flat", 5, settings, default_thresholds),
			).not.toThrow();
		});

		it("does not throw with disabled settings", () => {
			const settings = make_settings({ enabled: false });
			expect(() =>
				service.update(120, "Flat", 5, settings, default_thresholds),
			).not.toThrow();
		});
	});

	describe("stopFlashing", () => {
		it("sets isFlashing to false", () => {
			service.stopFlashing();
			expect(service.isFlashing).toBe(false);
		});
	});

	describe("reset", () => {
		it("stops flashing on reset", () => {
			service.reset();
			expect(service.isFlashing).toBe(false);
		});
	});
});
