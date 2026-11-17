import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { flushSync } from "svelte";
import { page as pageState } from "$app/state";
import { AlertRuleSeverity, type ActiveExcursionResponse } from "#lib/api/generated/nocturne-api-client.js";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";

let activeAlerts = $state<ActiveExcursionResponse[]>([]);
const calls = vi.hoisted(() => ({
	snoozes: [] as unknown[],
	seen: [] as unknown[],
	acks: [] as unknown[],
	sounds: [] as string[],
	stops: 0,
}));

vi.mock("#lib/api/generated/alerts.generated.remote.js", () => ({
	getActiveAlerts: () => remoteQuery(() => activeAlerts),
	acknowledgeExcursion: (arg: unknown) => {
		calls.acks.push(arg);
		return { updates: () => Promise.resolve() };
	},
	snoozeInstance: (arg: unknown) => {
		calls.snoozes.push(arg);
		return { updates: () => Promise.resolve() };
	},
	markSeen: (id: unknown) => {
		calls.seen.push(id);
		return Promise.resolve();
	},
}));

vi.mock("#lib/audio/alarm-sounds.js", () => ({
	playAlarmSound: (id: string) => {
		calls.sounds.push(id);
		return Promise.resolve();
	},
	stopAlarmSound: () => {
		calls.stops++;
	},
}));

import CriticalAlertTakeover from "./CriticalAlertTakeover.svelte";

function excursion(
	id: string,
	overrides: Partial<ActiveExcursionResponse> = {}
): ActiveExcursionResponse {
	return {
		id,
		ruleName: `Rule ${id}`,
		severity: AlertRuleSeverity.Critical,
		startedAt: new Date().toISOString(),
		activeInstances: [{ id: `inst-${id}` }],
		...overrides,
	};
}

describe("CriticalAlertTakeover", () => {
	beforeEach(() => {
		activeAlerts = [];
		pageState.data = {
			hasAccount: true,
			effectivePermissions: ["alerts.readwrite", "alerts.acknowledge"],
		};
		calls.snoozes = [];
		calls.seen = [];
		calls.acks = [];
		calls.sounds = [];
		calls.stops = 0;
	});

	it("covers the app for a critical alert and plays the urgent sound", async () => {
		activeAlerts = [excursion("a")];

		render(CriticalAlertTakeover);

		await expect.element(page.getByRole("alertdialog")).toBeVisible();
		await expect.element(page.getByRole("heading", { name: "Rule a" })).toBeVisible();
		expect(calls.sounds).toEqual(["alarm-urgent"]);
	});

	it("leaves warning and info alerts to the banner", async () => {
		activeAlerts = [
			excursion("w", { severity: AlertRuleSeverity.Warning }),
			excursion("i", { severity: AlertRuleSeverity.Info }),
		];

		render(CriticalAlertTakeover);

		await expect.element(page.getByRole("alertdialog")).not.toBeInTheDocument();
		expect(calls.sounds).toEqual([]);
	});

	it("stays down for an alert that is acknowledged, muted or snoozed", async () => {
		activeAlerts = [
			excursion("ack", { acknowledgedAt: new Date().toISOString() }),
			excursion("mute", { mutedByCaller: true }),
			excursion("snz", { snoozedUntil: new Date(Date.now() + 600_000).toISOString() }),
		];

		render(CriticalAlertTakeover);

		await expect.element(page.getByRole("alertdialog")).not.toBeInTheDocument();
	});

	it("acknowledges the alert shown", async () => {
		activeAlerts = [excursion("a")];

		render(CriticalAlertTakeover);
		await page.getByRole("button", { name: "Acknowledge" }).click();

		expect(calls.acks).toEqual([{ excursionId: "a", request: {} }]);
	});

	it("snoozes the excursion's instance", async () => {
		activeAlerts = [excursion("a")];

		render(CriticalAlertTakeover);
		await page.getByRole("button", { name: "Snooze for 30 minutes" }).click();

		expect(calls.snoozes).toEqual([{ instanceId: "inst-a", request: { minutes: 30 } }]);
	});

	it("offers a share or guest link only Seen", async () => {
		pageState.data = { hasAccount: false, effectivePermissions: ["alerts.read"] };
		activeAlerts = [excursion("g")];

		render(CriticalAlertTakeover);
		await page.getByRole("button", { name: "Seen" }).click();

		expect(calls.seen).toEqual(["g"]);
		expect(page.getByRole("button", { name: /Snooze for/ }).elements()).toHaveLength(0);
		await expect.element(page.getByRole("alertdialog")).not.toBeInTheDocument();
	});

	it("goes, and stops the sound, when the server closes the alert", async () => {
		activeAlerts = [excursion("a")];

		render(CriticalAlertTakeover);
		await expect.element(page.getByRole("alertdialog")).toBeVisible();

		activeAlerts = [];
		flushSync();

		await expect.element(page.getByRole("alertdialog")).not.toBeInTheDocument();
		expect(calls.stops).toBe(1);
	});

	it("plays the sound once for each alert, not on each poll", async () => {
		activeAlerts = [excursion("a")];

		render(CriticalAlertTakeover);
		await expect.element(page.getByRole("alertdialog")).toBeVisible();

		activeAlerts = [excursion("a")];
		flushSync();

		expect(calls.sounds).toEqual(["alarm-urgent"]);
	});

	it("names how many other critical alerts wait behind the one shown", async () => {
		activeAlerts = [excursion("a"), excursion("b"), excursion("c")];

		render(CriticalAlertTakeover);

		await expect.element(page.getByText("2 more critical alerts")).toBeVisible();
	});

	it("stays up when Escape is pressed", async () => {
		activeAlerts = [excursion("a")];

		render(CriticalAlertTakeover);
		await expect.element(page.getByRole("alertdialog")).toBeVisible();

		await userEvent.keyboard("{Escape}");
		await userEvent.keyboard("{Escape}");

		await expect.element(page.getByRole("alertdialog")).toBeVisible();
	});
});
