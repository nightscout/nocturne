import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { flushSync } from "svelte";
import { AlertRuleSeverity } from "$api-clients";
import type { DeviceCapabilityCatalog } from "$api-clients";
import type { ChannelDef } from "./types";

// Mock the generated remote queries before importing the component. The
// component reads `.current` reactively; `catalogCurrent` stays undefined to
// model the catalog request not having resolved yet.
let catalogCurrent: DeviceCapabilityCatalog | undefined;

vi.mock("$api/generated/linkedPlatforms.generated.remote", () => ({
	getLinkedPlatforms: () => ({
		get current() {
			return { platforms: [] };
		},
	}),
}));

vi.mock("$api/generated/clientDevices.generated.remote", () => ({
	getCapabilityCatalog: () => ({
		get current() {
			return catalogCurrent;
		},
	}),
}));

import ChannelsSection from "./ChannelsSection.svelte";

const catalog: DeviceCapabilityCatalog = {
	kinds: ["companion", "prelude"],
	capabilities: [
		{
			key: "notify",
			label: "Send a notification",
			requiredScope: "device.notify",
			kinds: ["prelude", "companion"],
			isHardware: false,
		},
	],
};

// The Device picker item's accessible name includes its description text.
const deviceItem = () =>
	page.getByRole("button", { name: /actuation intent/i });

describe("ChannelsSection", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		catalogCurrent = undefined;
	});

	it("disables the Device option while the capability catalog is not loaded", async () => {
		const channels = $state<ChannelDef[]>([]);
		render(ChannelsSection, { channels, severity: AlertRuleSeverity.Warning });

		await page.getByRole("button", { name: "Add channel" }).click();

		await expect.element(deviceItem()).toBeDisabled();
		await expect.element(page.getByText("Loading", { exact: true })).toBeVisible();
		expect(channels).toHaveLength(0);
	});

	it("adds a device channel seeded from the catalog once it has loaded", async () => {
		catalogCurrent = catalog;
		const channels = $state<ChannelDef[]>([]);
		render(ChannelsSection, { channels, severity: AlertRuleSeverity.Warning });

		await page.getByRole("button", { name: "Add channel" }).click();
		await expect.element(deviceItem()).not.toBeDisabled();
		await deviceItem().click();
		flushSync();

		expect(channels).toHaveLength(1);
		expect(channels[0].destination).toBe("companion");
		expect(channels[0].metadata?.capabilities).toContain("notify");
	});
});
