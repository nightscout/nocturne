import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { flushSync } from "svelte";
import { page as pageState } from "$app/state";
import type { ActiveExcursionResponse } from "$api-clients";
import { remoteQuery } from "$lib/test-stubs/remote-resource";

// Mock the generated remote surface before importing the component. The
// component reads `getActiveAlerts().current` inside an effect, so the backing
// value is `$state` — reassigning it re-runs that effect the way a poll would.
let activeAlerts = $state<ActiveExcursionResponse[]>([]);

const snoozes: { calls: unknown[] } = vi.hoisted(() => ({ calls: [] }));

vi.mock("$api/generated/alerts.generated.remote", () => ({
	getActiveAlerts: () => remoteQuery(() => activeAlerts),
	snoozeInstance: (arg: unknown) => {
		snoozes.calls.push(arg);
		return Promise.resolve();
	},
	acknowledgeExcursion: () => Promise.resolve(),
}));

import FiringToast from "./FiringToast.svelte";

function excursion(
	id: string,
	overrides: Partial<ActiveExcursionResponse> = {}
): ActiveExcursionResponse {
	return {
		id,
		ruleName: `Rule ${id}`,
		startedAt: new Date().toISOString(),
		...overrides,
	};
}

describe("FiringToast", () => {
	beforeEach(() => {
		activeAlerts = [];
		pageState.data = { effectivePermissions: ["alerts.readwrite"] };
		snoozes.calls = [];
	});

	it("surfaces a toast for a newly firing alert", async () => {
		activeAlerts = [excursion("a")];

		render(FiringToast);

		await expect.element(page.getByText("Rule a").first()).toBeVisible();
	});

	// Regression: the effect reads `queue` and used to reassign it with
	// `queue.filter(...)` on every run whenever any active alert carried
	// `acknowledgedAt`. `filter` returns a new array even when nothing matched,
	// so the write re-dirtied the effect's own dependency and Svelte aborted the
	// flush with effect_update_depth_exceeded. FiringToast is mounted by the
	// authenticated layout outside its error boundary, so that took down every
	// page in the app, not just the one being viewed.
	it("settles when an active alert is already acknowledged", async () => {
		activeAlerts = [excursion("acked", { acknowledgedAt: new Date().toISOString() })];

		expect(() => render(FiringToast)).not.toThrow();

		// An acknowledged alert never earns a card.
		await expect.element(page.getByText("Rule acked")).not.toBeInTheDocument();
	});

	it("drops a queued toast once that alert is acknowledged elsewhere", async () => {
		activeAlerts = [excursion("b")];

		render(FiringToast);
		await expect.element(page.getByText("Rule b").first()).toBeVisible();

		activeAlerts = [excursion("b", { acknowledgedAt: new Date().toISOString() })];
		flushSync();

		await expect.element(page.getByText("Rule b")).not.toBeInTheDocument();
	});

	it("offers Acknowledge to a member who manages alerts", async () => {
		activeAlerts = [excursion("c")];

		render(FiringToast);

		await expect.element(page.getByRole("button", { name: "Acknowledge" })).toBeVisible();
		await expect.element(page.getByRole("button", { name: "Mute for me" })).not.toBeInTheDocument();
	});

	it("offers only Mute for me to a member without alerts.readwrite", async () => {
		pageState.data = { effectivePermissions: ["glucose.read", "device.notify"] };
		activeAlerts = [excursion("d")];

		render(FiringToast);

		await expect.element(page.getByRole("button", { name: "Mute for me" })).toBeVisible();
		await expect.element(page.getByRole("button", { name: "Acknowledge" })).not.toBeInTheDocument();
	});

	it("never raises a card for an alert the member already muted", async () => {
		pageState.data = { effectivePermissions: ["glucose.read", "device.notify"] };
		activeAlerts = [excursion("e", { mutedByCaller: true })];

		render(FiringToast);

		await expect.element(page.getByText("Rule e")).not.toBeInTheDocument();
	});

	it("snoozes the excursion's instance, not the excursion", async () => {
		activeAlerts = [
			excursion("exc-1", { activeInstances: [{ id: "inst-1" }] }),
		];

		render(FiringToast);
		await page.getByRole("button", { name: "15m" }).click();

		expect(snoozes.calls).toEqual([
			{ instanceId: "inst-1", request: { minutes: 15 } },
		]);
		await expect.element(page.getByText("Rule exc-1")).not.toBeInTheDocument();
	});

	it("offers no snooze when the server reports no active instance", async () => {
		activeAlerts = [excursion("bare", { activeInstances: [] })];

		render(FiringToast);

		await expect.element(page.getByText("Rule bare").first()).toBeVisible();
		await expect
			.element(page.getByRole("button", { name: "15m" }))
			.not.toBeInTheDocument();
	});

	it("shows no card for an alert the server reports as snoozed", async () => {
		activeAlerts = [
			excursion("zz", { snoozedUntil: new Date(Date.now() + 600_000).toISOString() }),
		];

		render(FiringToast);

		await expect.element(page.getByText("Rule zz")).not.toBeInTheDocument();
	});

	it("drops a queued card once the alert is snoozed elsewhere", async () => {
		activeAlerts = [excursion("c")];

		render(FiringToast);
		await expect.element(page.getByText("Rule c").first()).toBeVisible();

		activeAlerts = [
			excursion("c", { snoozedUntil: new Date(Date.now() + 600_000).toISOString() }),
		];
		flushSync();

		await expect.element(page.getByText("Rule c")).not.toBeInTheDocument();
	});

	it("resurfaces the card when the server stops reporting the snooze", async () => {
		activeAlerts = [
			excursion("d", { snoozedUntil: new Date(Date.now() + 600_000).toISOString() }),
		];

		render(FiringToast);
		await expect.element(page.getByText("Rule d")).not.toBeInTheDocument();

		activeAlerts = [excursion("d", { snoozedUntil: undefined })];
		flushSync();

		await expect.element(page.getByText("Rule d").first()).toBeVisible();
	});

	it("keeps a card closed with X closed while the alert stays unsnoozed", async () => {
		activeAlerts = [excursion("e")];

		render(FiringToast);
		await page
			.getByRole("button", { name: "Close this notification without acknowledging" })
			.click();
		await expect.element(page.getByText("Rule e")).not.toBeInTheDocument();

		activeAlerts = [excursion("e")];
		flushSync();

		await expect.element(page.getByText("Rule e")).not.toBeInTheDocument();
	});
});
