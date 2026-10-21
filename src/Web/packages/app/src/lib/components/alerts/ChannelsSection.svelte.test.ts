import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { flushSync } from "svelte";
import { AlertRuleSeverity, ChannelStatus, ChannelType } from "$api-clients";
import type {
	ChannelStatusResponse,
	DeviceCapabilityCatalog,
	InAppRecipientResponse,
} from "$api-clients";
import type { ChannelDef } from "./types";
import { page as pageState } from "$app/state";

const ME = "0199a1b2-0000-7000-8000-000000000001";
const ALEX = "0199a1b2-0000-7000-8000-00000000000a";
const SAM = "0199a1b2-0000-7000-8000-00000000000b";

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

let recipientsCurrent: InAppRecipientResponse[] | undefined;
const getInAppRecipients = vi.fn(() => ({
	get current() {
		return recipientsCurrent;
	},
}));

vi.mock("$api/generated/alertRules.generated.remote", () => ({
	getInAppRecipients: () => getInAppRecipients(),
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
	page.getByRole("menuitem", { name: /actuation intent/i });

describe("ChannelsSection", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		catalogCurrent = undefined;
		catalogError = undefined;
		statusCurrent = loadedStatuses();
		statusError = undefined;
		recipientsCurrent = [
			{ subjectId: ME, name: "Jo", canReceive: true },
			{ subjectId: ALEX, name: "Alex", canReceive: true },
			{ subjectId: SAM, name: "Sam", canReceive: true },
		];
		pageState.data = {
			effectivePermissions: ["alerts.readwrite"],
			user: { subjectId: ME },
		} as unknown as typeof pageState.data;
	});

	function inApp(over: Partial<ChannelDef> = {}): ChannelDef {
		return { _uid: "in-app", channelType: ChannelType.InApp, destination: "", destinationLabel: "", ...over };
	}

	it("shows the member an in-app channel is sent to", async () => {
		const channels = $state<ChannelDef[]>([inApp({ destination: SAM })]);
		render(ChannelsSection, { channels, severity: AlertRuleSeverity.Warning });

		await expect.element(page.getByTestId("in-app-recipient-trigger")).toHaveTextContent("Sam");
	});

	it("sends a new in-app channel to whoever saves the rule", async () => {
		const channels = $state<ChannelDef[]>([inApp()]);
		render(ChannelsSection, { channels, severity: AlertRuleSeverity.Warning });

		await expect.element(page.getByTestId("in-app-recipient-trigger")).toHaveTextContent("Me");
	});

	it("shows a channel routed to the caller as Me and lists the caller once", async () => {
		const channels = $state<ChannelDef[]>([inApp({ destination: ME })]);
		render(ChannelsSection, { channels, severity: AlertRuleSeverity.Warning });

		await expect.element(page.getByTestId("in-app-recipient-trigger")).toHaveTextContent("Me");
		await page.getByTestId("in-app-recipient-trigger").click();
		await expect.element(page.getByRole("option", { name: "Alex" })).toBeVisible();
		expect(page.getByRole("option", { name: "Jo" }).elements()).toHaveLength(0);
	});

	it("warns that a channel whose recipient cannot receive goes to the saver", async () => {
		const departed = "0199a1b2-0000-7000-8000-00000000000c";
		const channels = $state<ChannelDef[]>([
			inApp({ destination: departed, recipient: { subjectId: departed, canReceive: false } }),
		]);
		render(ChannelsSection, { channels, severity: AlertRuleSeverity.Warning });

		await expect
			.element(page.getByTestId("in-app-recipient-trigger"))
			.toHaveTextContent("Unavailable member");
		await expect
			.element(page.getByText(/can no longer receive alerts on this site/i))
			.toBeVisible();
	});

	it("warns about a stored channel that names no one instead of showing Me", async () => {
		const channels = $state<ChannelDef[]>([
			inApp({ destination: "", recipient: { subjectId: undefined, canReceive: false } }),
		]);
		render(ChannelsSection, { channels, severity: AlertRuleSeverity.Warning });

		await expect
			.element(page.getByTestId("in-app-recipient-trigger"))
			.toHaveTextContent("No recipient");
		await expect.element(page.getByText(/has no recipient/i)).toBeVisible();
	});

	it("routes an in-app channel to the member picked", async () => {
		const channels = $state<ChannelDef[]>([inApp()]);
		render(ChannelsSection, { channels, severity: AlertRuleSeverity.Warning });

		await page.getByTestId("in-app-recipient-trigger").click();
		await page.getByRole("option", { name: "Alex" }).click();

		expect(channels[0].destination).toBe(ALEX);
	});

	it("does not ask for recipients without an in-app channel or the right to edit", async () => {
		const noInApp = $state<ChannelDef[]>([]);
		render(ChannelsSection, { channels: noInApp, severity: AlertRuleSeverity.Warning });
		expect(getInAppRecipients).not.toHaveBeenCalled();

		pageState.data = { effectivePermissions: ["alerts.read"] } as unknown as typeof pageState.data;
		const readOnly = $state<ChannelDef[]>([inApp()]);
		render(ChannelsSection, { channels: readOnly, severity: AlertRuleSeverity.Warning });
		expect(getInAppRecipients).not.toHaveBeenCalled();
	});

	it("disables every option until the channel-status query resolves", async () => {
		statusCurrent = undefined;
		const channels = $state<ChannelDef[]>([]);
		render(ChannelsSection, { channels, severity: AlertRuleSeverity.Warning });

		await page.getByRole("button", { name: "Add channel" }).click();

		await expect
			.element(page.getByRole("menuitem", { name: /POST to a custom URL/i }))
			.toBeDisabled();
		expect(channels).toHaveLength(0);
	});

	it("omits a channel kind the API does not offer", async () => {
		statusCurrent = loadedStatuses([ChannelType.SlackChannel]);
		const channels = $state<ChannelDef[]>([]);
		render(ChannelsSection, { channels, severity: AlertRuleSeverity.Warning });

		await page.getByRole("button", { name: "Add channel" }).click();

		await expect
			.element(page.getByRole("menuitem", { name: /POST to a custom URL/i }))
			.toBeVisible();
		expect(
			page.getByRole("menuitem", { name: /Post to a Slack channel/i }).elements(),
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
