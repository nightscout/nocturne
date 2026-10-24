import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { page as pageState } from "$app/state";
import { AlertConditionType, AlertReceiptActorKind, type ActiveExcursionResponse } from "$api-clients";
import { remoteQuery } from "$lib/test-stubs/remote-resource";

let activeAlerts = $state<ActiveExcursionResponse[]>([]);
const seenCalls: { ids: unknown[] } = vi.hoisted(() => ({ ids: [] }));

vi.mock("$api/generated/alerts.generated.remote", () => ({
	getActiveAlerts: () => remoteQuery(() => activeAlerts),
	acknowledgeExcursion: () => ({ updates: () => Promise.resolve() }),
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

	it("names the condition that fired", async () => {
		pageState.data = { hasAccount: true, effectivePermissions: ["alerts.readwrite"] };
		activeAlerts = [
			{ ...firing, id: "roc", ruleName: "Rule C", conditionType: AlertConditionType.RateOfChange },
			{ ...firing, id: "loss", ruleName: "Rule D", conditionType: AlertConditionType.SignalLoss },
			{ ...firing, id: "comp", ruleName: "Rule E", conditionType: AlertConditionType.Composite },
		];

		render(AlertBanner);

		await expect.element(page.getByText("Rapid change")).toBeVisible();
		await expect.element(page.getByText("Signal lost")).toBeVisible();
		await expect.element(page.getByText("Composite alert")).toBeVisible();
	});
});
