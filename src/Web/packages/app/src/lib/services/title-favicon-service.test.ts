import { describe, it, expect, beforeEach, vi, afterEach } from "vitest";
import { TitleFaviconService } from "./title-favicon-service.svelte";
import type { TitleFaviconSettings } from "$lib/stores/serverSettings";
import type { AlarmVisualSettings } from "$lib/types/alarm-profile";
import { GlucoseStatus } from "$lib/api/generated/nocturne-api-client";
import { getGlucoseTileVariant } from "$lib/utils/glucose-status";
import { renderGlucoseIcon } from "@nocturne/ui/glucose-icon";

// Mock $app/environment
vi.mock("$app/environment", () => ({ browser: true }));

// Mock formatting utils - they depend on global glucose units
vi.mock("$lib/utils/formatting", () => ({
	bg: (mgdl: number) => Math.round(mgdl),
	bgDelta: (delta: number) => (delta > 0 ? `+${Math.round(delta)}` : `${Math.round(delta)}`),
}));

vi.mock("@nocturne/ui/glucose-icon", () => ({ renderGlucoseIcon: vi.fn(() => "data:") }));

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

function stubDom(): void {
	vi.stubGlobal("getComputedStyle", () => ({ getPropertyValue: (name: string) => name }));
	vi.stubGlobal("document", {
		title: "",
		documentElement: {},
		head: { appendChild: () => {} },
		createElement: () => ({ getContext: () => ({}) }),
		querySelector: () => ({ href: "" }),
	});
}

const alarmVisual: AlarmVisualSettings = {
	screenFlash: true,
	flashColor: "",
	flashIntervalMs: 1000,
	persistentBanner: true,
	wakeScreen: true,
	showEmergencyContacts: false,
};

describe("TitleFaviconService", () => {
	let service: TitleFaviconService;

	beforeEach(() => {
		service = new TitleFaviconService();
	});

	afterEach(() => {
		service.destroy();
		vi.unstubAllGlobals();
	});

	describe("favicon fill", () => {
		beforeEach(() => {
			stubDom();
			vi.mocked(renderGlucoseIcon).mockClear();
			service.initialize();
		});

		function fillFor(status: GlucoseStatus | undefined, bg = 170): string {
			service.update(
				bg,
				"Flat",
				0,
				make_settings({ faviconEnabled: true, faviconColorCoded: true }),
				getGlucoseTileVariant(status),
			);
			return vi.mocked(renderGlucoseIcon).mock.lastCall![0].bgColor;
		}

		it.each([
			[GlucoseStatus.UrgentLow, "--glucose-very-low"],
			[GlucoseStatus.Low, "--glucose-low"],
			[GlucoseStatus.InRange, "--glucose-in-range"],
			[GlucoseStatus.High, "--glucose-high"],
			[GlucoseStatus.UrgentHigh, "--glucose-very-high"],
			[GlucoseStatus.Stale, "--muted-foreground"],
			[GlucoseStatus.Unknown, "--muted-foreground"],
		])("fills a %s reading with %s", (status, token) => {
			expect(fillFor(status)).toBe(token);
		});

		it("takes its colour from the server status, not the value", () => {
			expect(fillFor(GlucoseStatus.InRange, 170)).toBe("--glucose-in-range");
			expect(fillFor(GlucoseStatus.High, 170)).toBe("--glucose-high");
			expect(fillFor(GlucoseStatus.InRange, 40)).toBe("--glucose-in-range");
		});

		it("is neutral before the status for the reading has loaded", () => {
			expect(fillFor(undefined, 40)).toBe("--muted-foreground");
		});
	});

	describe("syncAlarmFlash", () => {
		beforeEach(() => {
			stubDom();
			service.initialize();
		});

		it("keeps flashing while the new reading's status loads, and stops on a loaded non-urgent one", () => {
			service.syncAlarmFlash(GlucoseStatus.UrgentLow, alarmVisual);
			expect(service.isFlashing).toBe(true);

			service.syncAlarmFlash(undefined, alarmVisual);
			expect(service.isFlashing).toBe(true);

			service.syncAlarmFlash(GlucoseStatus.InRange, alarmVisual);
			expect(service.isFlashing).toBe(false);
		});

		it.each([GlucoseStatus.UrgentLow, GlucoseStatus.UrgentHigh])("flashes for %s", (status) => {
			service.syncAlarmFlash(status, alarmVisual);
			expect(service.isFlashing).toBe(true);
		});

		it.each([
			GlucoseStatus.Low,
			GlucoseStatus.InRange,
			GlucoseStatus.High,
			GlucoseStatus.Stale,
			GlucoseStatus.Unknown,
		])("stops for %s", (status) => {
			service.syncAlarmFlash(GlucoseStatus.UrgentHigh, alarmVisual);
			service.syncAlarmFlash(status, alarmVisual);
			expect(service.isFlashing).toBe(false);
		});

		it("does not start without a loaded status", () => {
			service.syncAlarmFlash(undefined, alarmVisual);
			expect(service.isFlashing).toBe(false);
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
				service.update(120, "Flat", 5, settings, "in-range"),
			).not.toThrow();
		});

		it("does not throw with disabled settings", () => {
			const settings = make_settings({ enabled: false });
			expect(() =>
				service.update(120, "Flat", 5, settings, "in-range"),
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
