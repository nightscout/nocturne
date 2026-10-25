import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { flushSync } from "svelte";
import { page as pageState } from "$app/state";
import { AlertConditionType, AlertReceiptActorKind, type ActiveExcursionResponse } from "$api-clients";
import { formatDateTimeCompact } from "$lib/utils/formatting";
import { remoteQuery } from "$lib/test-stubs/remote-resource";

let activeAlerts = $state<ActiveExcursionResponse[]>([]);
const seenCalls: { ids: unknown[] } = vi.hoisted(() => ({ ids: [] }));
const snoozes: { calls: unknown[] } = vi.hoisted(() => ({ calls: [] }));

vi.mock("$api/generated/alerts.generated.remote", () => ({
	getActiveAlerts: () => remoteQuery(() => activeAlerts),
	acknowledgeExcursion: () => ({ updates: () => Promise.resolve() }),
	snoozeInstance: (arg: unknown) => {
		snoozes.calls.push(arg);
		return Promise.resolve();
	},
	markSeen: (id: unknown) => {
		seenCalls.ids.push(id);
		return Promise.resolve();
	},
}));

import AlertBanner from "./AlertBanner.svelte";

const firing: ActiveExcursionResponse = {
	id: "e",
	ruleName: "Low",
	startedAt: new Date().toISOString(),
};

describe("AlertBanner", () => {
	beforeEach(() => {
		seenCalls.ids = [];
		snoozes.calls = [];
		activeAlerts = [];
	});

	it("tells a member who has seen the alarm", async () => {
		pageState.data = { hasAccount: true, effectivePermissions: ["alerts.readwrite"] };
		activeAlerts = [{
			...firing,
			receipts: [
				{ actorKind: AlertReceiptActorKind.PublicLink, seenAt: new Date().toISOString() },
				{ actorKind: AlertReceiptActorKind.Guest, actorLabel: "Grandma", seenAt: new Date().toISOString() },
			],
		}];

		render(AlertBanner);

		await expect
			.element(page.getByText("Seen by Public link, Grandma (guest link)"))
			.toBeVisible();
		await expect.element(page.getByRole("button", { name: "Acknowledge" })).toBeVisible();
	});

	it("shows the age in one unit and the start time as its title", async () => {
		pageState.data = { hasAccount: true, effectivePermissions: ["alerts.readwrite"] };
		const startedAt = new Date(Date.now() - (3 * 60 + 5) * 60_000);
		activeAlerts = [{ ...firing, startedAt: startedAt.toISOString() }];

		render(AlertBanner);

		const age = new Intl.RelativeTimeFormat("en", { style: "narrow", numeric: "auto" }).format(-3, "hour");
		const shown = page.getByText(age, { exact: true });
		await expect.element(shown).toBeVisible();
		await expect.element(shown).toHaveAttribute("title", formatDateTimeCompact(startedAt));
	});

	it("labels a threshold alert by what it watches, not its condition type", async () => {
		pageState.data = { hasAccount: true, effectivePermissions: ["alerts.readwrite"] };
		activeAlerts = [{ ...firing, conditionType: AlertConditionType.Threshold }];

		render(AlertBanner);

		await expect.element(page.getByText("Glucose threshold")).toBeVisible();
		expect(page.getByText("threshold", { exact: true }).elements()).toHaveLength(0);
	});

	it("labels a leaf alert with the rule builder's name for it", async () => {
		pageState.data = { hasAccount: true, effectivePermissions: ["alerts.readwrite"] };
		activeAlerts = [{ ...firing, conditionType: AlertConditionType.SignalLoss }];

		render(AlertBanner);

		await expect.element(page.getByText("Signal loss")).toBeVisible();
	});

	it("offers a share or guest link only Seen", async () => {
		pageState.data = { hasAccount: false, effectivePermissions: ["alerts.read"] };
		activeAlerts = [firing];

		render(AlertBanner);

		await page.getByRole("button", { name: "Seen" }).click();

		expect(seenCalls.ids).toEqual(["e"]);
		expect(page.getByRole("button", { name: "Mute for me" }).elements()).toHaveLength(0);
	});

	it("leaves out an alarm a guest link has already seen", async () => {
		pageState.data = { hasAccount: false, isShareHost: false, effectivePermissions: ["alerts.read"] };
		activeAlerts = [{ ...firing, id: "guest-seen", receipts: [{ seenAt: new Date().toISOString() }] }];

		render(AlertBanner);

		await expect.element(page.getByText("Low")).not.toBeInTheDocument();
	});

	it("keeps an alarm up on a public-link screen that another screen marked seen", async () => {
		pageState.data = { hasAccount: false, isShareHost: true, effectivePermissions: ["alerts.read"] };
		activeAlerts = [{ ...firing, id: "other-screen", receipts: [{ seenAt: new Date().toISOString() }] }];

		render(AlertBanner);

		await expect.element(page.getByText("Low")).toBeVisible();
		await page.getByRole("button", { name: "Seen" }).click();
		await expect.element(page.getByText("Low")).not.toBeInTheDocument();
	});

	it("snoozes the excursion's instance, not the excursion", async () => {
		pageState.data = { hasAccount: true, effectivePermissions: ["alerts.readwrite", "alerts.acknowledge"] };
		activeAlerts = [{ ...firing, id: "exc-1", activeInstances: [{ id: "inst-1" }] }];

		render(AlertBanner);
		await page.getByRole("button", { name: "Snooze for 15 minutes" }).click();

		expect(snoozes.calls).toEqual([{ instanceId: "inst-1", request: { minutes: 15 } }]);
	});

	it("offers no snooze without an active instance or the scope to snooze", async () => {
		pageState.data = { hasAccount: true, effectivePermissions: ["alerts.readwrite", "alerts.acknowledge"] };
		activeAlerts = [{ ...firing, activeInstances: [] }];
		const bare = render(AlertBanner);
		await expect.element(page.getByText("Low")).toBeVisible();
		expect(page.getByRole("button", { name: /Snooze for/ }).elements()).toHaveLength(0);
		bare.unmount();

		pageState.data = { hasAccount: false, effectivePermissions: ["alerts.read"] };
		activeAlerts = [{ ...firing, id: "guest-no-snooze", activeInstances: [{ id: "i" }] }];
		render(AlertBanner);
		await expect.element(page.getByRole("button", { name: "Seen" })).toBeVisible();
		expect(page.getByRole("button", { name: /Snooze for/ }).elements()).toHaveLength(0);
	});

	it("keeps a snoozed alert listed without offering another snooze", async () => {
		pageState.data = { hasAccount: true, effectivePermissions: ["alerts.readwrite", "alerts.acknowledge"] };
		activeAlerts = [{
			...firing,
			activeInstances: [{ id: "i" }],
			snoozedUntil: new Date(Date.now() + 600_000).toISOString(),
		}];

		render(AlertBanner);

		await expect.element(page.getByText(/Snoozed until/)).toBeVisible();
		expect(page.getByRole("button", { name: /Snooze for/ }).elements()).toHaveLength(0);
	});

	it("drops an alert once the server closes it", async () => {
		pageState.data = { hasAccount: true, effectivePermissions: ["alerts.readwrite"] };
		activeAlerts = [firing];

		render(AlertBanner);
		await expect.element(page.getByText("Low")).toBeVisible();

		activeAlerts = [];
		flushSync();

		await expect.element(page.getByText("Low")).not.toBeInTheDocument();
	});
});
