import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { flushSync } from "svelte";
import { AlertRuleSeverity, ChannelStatus, ChannelType } from "$api-clients";
import type { ChannelStatusResponse, DeviceCapabilityCatalog } from "$api-clients";
import type { ChannelDef } from "./types";

// Mock the generated remote queries before importing the component. The
// component reads `.current` and `.error` reactively; `catalogCurrent` stays
// undefined to model the catalog request not having resolved yet, and
// `catalogError` set with `catalogCurrent` undefined models a failed query.
// `statusCurrent` models the same two states for the channel-status query.
let catalogCurrent: DeviceCapabilityCatalog | undefined;
let catalogError: Error | undefined;
let statusCurrent: ChannelStatusResponse | undefined;
let statusError: Error | undefined;

vi.mock("$api/generated/linkedPlatforms.generated.remote", () => ({
	getLinkedPlatforms: () => ({
		get current() {
			return { platforms: [] };
		},
	}),
}));

vi.mock("$api/generated/systems.generated.remote", () => ({
	getChannelStatuses: () => ({
		get current() {
			return statusCurrent;
		},
		get error() {
			return statusError;
		},
	}),
}));

vi.mock("$api/generated/clientDevices.generated.remote", () => ({
	getCapabilityCatalog: () => ({
		get current() {
			return catalogCurrent;
		},
		get error() {
			return catalogError;
		},
	}),
}));

import ChannelsSection from "./ChannelsSection.svelte";
import { CHANNEL_META } from "./channelMeta";

/** Mirrors the server's UserSupplied destination modes. */
const REQUIRES_DESTINATION = new Set<ChannelType>([
	ChannelType.Webhook,
	ChannelType.DiscordChannel,
	ChannelType.SlackChannel,
	ChannelType.TelegramGroup,
	ChannelType.WhatsAppDm,
	ChannelType.ResendEmail,
]);

function loadedStatuses(notOffered: ChannelType[] = []): ChannelStatusResponse {
	return {
		channels: CHANNEL_META.map((m) => ({
			channelType: m.type,
			status: ChannelStatus.Available,
			offered: !notOffered.includes(m.type),
			requiresDestination: REQUIRES_DESTINATION.has(m.type),
			requiresLink: false,
		})),
	};
}

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
		catalogError = undefined;
		statusCurrent = loadedStatuses();
		statusError = undefined;
	});

	it("disables every option until the channel-status query resolves", async () => {
		statusCurrent = undefined;
		const channels = $state<ChannelDef[]>([]);
		render(ChannelsSection, { channels, severity: AlertRuleSeverity.Warning });

		await page.getByRole("button", { name: "Add channel" }).click();

		await expect
			.element(page.getByRole("button", { name: /POST to a custom URL/i }))
			.toBeDisabled();
		expect(channels).toHaveLength(0);
	});

	it("omits a channel kind the API does not offer", async () => {
		statusCurrent = loadedStatuses([ChannelType.SlackChannel]);
		const channels = $state<ChannelDef[]>([]);
		render(ChannelsSection, { channels, severity: AlertRuleSeverity.Warning });

		await page.getByRole("button", { name: "Add channel" }).click();

		await expect
			.element(page.getByRole("button", { name: /POST to a custom URL/i }))
			.toBeVisible();
		expect(
			page.getByRole("button", { name: /Post to a Slack channel/i }).elements(),
		).toHaveLength(0);
	});

	it("flags a Slack channel destination that is not a channel ID", async () => {
		const channels = $state<ChannelDef[]>([
			{
				_uid: "slack",
				channelType: ChannelType.SlackChannel,
				destination: "#general",
				destinationLabel: "",
			},
		]);
		render(ChannelsSection, { channels, severity: AlertRuleSeverity.Warning });

		await expect
			.element(page.getByText("A channel ID starts with C, G or D."))
			.toBeVisible();
	});

	it("asks for no destination on a DM the server resolves from the linked identity", async () => {
		const channels = $state<ChannelDef[]>([
			{
				_uid: "slack-dm",
				channelType: ChannelType.SlackDm,
				destination: "",
				destinationLabel: "",
			},
		]);
		render(ChannelsSection, { channels, severity: AlertRuleSeverity.Warning });

		await expect
			.element(page.getByText("Sent to the Slack account you linked."))
			.toBeVisible();
		expect(page.getByLabelText("Channel ID").elements()).toHaveLength(0);
	});

	it("disables the Device option while the capability catalog is not loaded", async () => {
		const channels = $state<ChannelDef[]>([]);
		render(ChannelsSection, { channels, severity: AlertRuleSeverity.Warning });

		await page.getByRole("button", { name: "Add channel" }).click();

		await expect.element(deviceItem()).toBeDisabled();
		await expect.element(page.getByText("Loading", { exact: true })).toBeVisible();
		expect(channels).toHaveLength(0);
	});

	it("shows Unavailable and keeps the Device option disabled when the catalog query fails", async () => {
		catalogError = new Error("catalog fetch failed");
		const channels = $state<ChannelDef[]>([]);
		render(ChannelsSection, { channels, severity: AlertRuleSeverity.Warning });

		await page.getByRole("button", { name: "Add channel" }).click();

		await expect.element(deviceItem()).toBeDisabled();
		await expect
			.element(page.getByText("Unavailable", { exact: true }))
			.toBeVisible();
		expect(
			page.getByText("Loading", { exact: true }).elements(),
		).toHaveLength(0);
		expect(channels).toHaveLength(0);
	});

	it("flags a Discord channel destination that is not a channel ID", async () => {
		const channels = $state<ChannelDef[]>([
			{
				_uid: "discord",
				channelType: ChannelType.DiscordChannel,
				destination: "https://discord.com/api/webhooks/1/abc",
				destinationLabel: "",
			},
		]);
		render(ChannelsSection, { channels, severity: AlertRuleSeverity.Warning });

		await expect
			.element(page.getByText("A channel ID is 17-20 digits."))
			.toBeVisible();
	});

	it("shows the channel-ID hint once the Discord destination is a snowflake", async () => {
		const channels = $state<ChannelDef[]>([
			{
				_uid: "discord",
				channelType: ChannelType.DiscordChannel,
				destination: "1234567890123456789",
				destinationLabel: "",
			},
		]);
		render(ChannelsSection, { channels, severity: AlertRuleSeverity.Warning });

		await expect
			.element(page.getByText(/Copy Channel ID/))
			.toBeVisible();
		expect(
			page.getByText("A channel ID is 17-20 digits.").elements(),
		).toHaveLength(0);
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
