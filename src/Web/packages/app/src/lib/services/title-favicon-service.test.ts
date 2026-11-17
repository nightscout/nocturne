import { describe, it, expect, beforeEach, vi, afterEach } from "vitest";
import { TitleFaviconService } from "./title-favicon-service.svelte";
import type { TitleFaviconSettings } from "#lib/stores/serverSettings.js";
import type { AlarmVisualSettings } from "#lib/types/alarm-profile.js";
import { GlucoseStatus } from "#lib/api/generated/nocturne-api-client.js";
import { getGlucoseTileVariant } from "#lib/utils/glucose-status.js";
import { renderGlucoseIcon } from "@nocturne/ui/glucose-icon";

// Mock $app/env
vi.mock("$app/env", () => ({ browser: true }));

// Mock formatting utils - they depend on global glucose units
vi.mock("#lib/utils/formatting.js", () => ({
	bg: (mgdl: number) => Math.round(mgdl),
	bgDelta: (delta: number) => (delta > 0 ? `+${Math.round(delta)}` : `${Math.round(delta)}`),
}));

vi.mock("@nocturne/ui/glucose-icon", () => ({ renderGlucoseIcon: vi.fn(() => "data:") }));

const engineHost = vi.hoisted(() => {
	const host = {
		booting: null as Promise<void> | null,
		async whenNotBooting(): Promise<void> {
			while (host.booting) await host.booting;
		},
	};
	return host;
});

vi.mock("water-ui", () => ({ getEngineHost: () => engineHost }));

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

class FakeMutationObserver {
	static instances: FakeMutationObserver[] = [];
	observed: unknown = null;
	options: MutationObserverInit | undefined;
	disconnected = false;

	constructor(private callback: () => void) {
		FakeMutationObserver.instances.push(this);
	}

	observe(target: unknown, options?: MutationObserverInit): void {
		this.observed = target;
		this.options = options;
	}

	disconnect(): void {
		this.disconnected = true;
	}

	fire(): void {
		if (!this.disconnected) this.callback();
	}
}

function stubDom(): void {
	FakeMutationObserver.instances = [];
	vi.stubGlobal("MutationObserver", FakeMutationObserver);
	vi.stubGlobal("getComputedStyle", () => ({ getPropertyValue: (name: string) => name }));
	vi.stubGlobal("document", {
		title: "",
		documentElement: {},
		head: { appendChild: () => {} },
		createElement: () => ({ getContext: () => ({}) }),
		querySelector: vi.fn(() => ({ href: "" })),
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

	describe("during an engine boot", () => {
		let link: { href: string };
		let endBoot: () => void;

		beforeEach(() => {
			stubDom();
			link = { href: "" };
			vi.mocked(document.querySelector).mockReturnValue(link as unknown as Element);
			vi.mocked(renderGlucoseIcon).mockReset().mockImplementation(({ text }) => `data:${text}`);
			service.initialize();
			engineHost.booting = new Promise<void>((resolve) => {
				endBoot = () => {
					engineHost.booting = null;
					resolve();
				};
			});
		});

		afterEach(() => {
			endBoot();
		});

		const settings = make_settings({ faviconEnabled: true, faviconShowBg: true });

		it("draws the favicon once the boot is over, and only the latest reading", async () => {
			service.update(100, "Flat", 0, settings, "in-range");
			service.update(120, "Flat", 0, settings, "in-range");
			await Promise.resolve();
			expect(renderGlucoseIcon).not.toHaveBeenCalled();

			endBoot();
			await vi.waitFor(() => expect(link.href).toBe("data:120"));
			expect(renderGlucoseIcon).toHaveBeenCalledOnce();
		});

		it("holds the alarm flash's favicon until the boot is over", async () => {
			service.update(50, "Flat", 0, settings, "very-low");
			service.syncAlarmFlash(GlucoseStatus.UrgentLow, alarmVisual);
			await Promise.resolve();
			expect(renderGlucoseIcon).not.toHaveBeenCalled();

			endBoot();
			await vi.waitFor(() => expect(link.href).toBe("data:50"));
			expect(renderGlucoseIcon).toHaveBeenCalledOnce();
		});

		it("draws at once with no boot in progress", () => {
			endBoot();
			service.update(140, "Flat", 0, settings, "in-range");
			expect(link.href).toBe("data:140");
		});
	});

	describe("root class changes", () => {
		let palette: Record<string, string>;

		beforeEach(() => {
			palette = { "--glucose-in-range": "green", "--foreground": "black" };
			stubDom();
			vi.stubGlobal("getComputedStyle", () => ({
				getPropertyValue: (name: string) => palette[name] ?? name,
			}));
			vi.mocked(renderGlucoseIcon).mockClear();
			service.initialize();
		});

		const settings = () => make_settings({ faviconEnabled: true, faviconColorCoded: true });

		it("watches only the class attribute of the document root", () => {
			const [observer] = FakeMutationObserver.instances;
			expect(observer.observed).toBe(document.documentElement);
			expect(observer.options).toEqual({ attributes: true, attributeFilter: ["class"] });
		});

		it("repaints the last reading with the newly resolved colour", () => {
			service.update(110, "Flat", 0, settings(), "in-range");
			expect(vi.mocked(renderGlucoseIcon).mock.lastCall![0].bgColor).toBe("green");
			const painted = vi.mocked(renderGlucoseIcon).mock.calls.length;

			palette["--glucose-in-range"] = "teal";
			FakeMutationObserver.instances[0].fire();

			expect(vi.mocked(renderGlucoseIcon).mock.calls.length).toBe(painted + 1);
			expect(vi.mocked(renderGlucoseIcon).mock.lastCall![0].bgColor).toBe("teal");
		});

		it("keeps the grey fill of a stale reading", () => {
			palette["--muted-foreground"] = "grey";
			service.update(110, "Flat", 0, settings(), "in-range", false, true);
			palette["--muted-foreground"] = "slate";
			FakeMutationObserver.instances[0].fire();
			expect(vi.mocked(renderGlucoseIcon).mock.lastCall![0].bgColor).toBe("slate");
		});

		it("does not repaint before a reading", () => {
			FakeMutationObserver.instances[0].fire();
			expect(renderGlucoseIcon).not.toHaveBeenCalled();
		});

		it("does not repaint after reset", () => {
			service.update(110, "Flat", 0, settings(), "in-range");
			service.reset();
			vi.mocked(renderGlucoseIcon).mockClear();
			FakeMutationObserver.instances[0].fire();
			expect(renderGlucoseIcon).not.toHaveBeenCalled();
		});

		it.each([
			["the favicon", { faviconEnabled: false, faviconColorCoded: true }],
			["the feature", { enabled: false, faviconEnabled: true, faviconColorCoded: true }],
		])("does not repaint once %s is turned off", (_, off) => {
			service.update(110, "Flat", 0, settings(), "in-range");
			service.update(110, "Flat", 0, make_settings(off), "in-range");
			vi.mocked(renderGlucoseIcon).mockClear();
			FakeMutationObserver.instances[0].fire();
			expect(renderGlucoseIcon).not.toHaveBeenCalled();
		});

		it("stops watching when destroyed", () => {
			service.destroy();
			expect(FakeMutationObserver.instances[0].disconnected).toBe(true);
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
