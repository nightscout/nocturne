import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { flushSync } from "svelte";
import { AlertChannelState, AlertRuleSeverity, ChannelStatus, ChannelType } from "@nocturne/api-client";
import type {
	ChannelStatusResponse,
	DeviceCapabilityCatalog,
	InAppRecipientResponse,
} from "@nocturne/api-client";
import type { ChannelDef } from "./types";
import { page as pageState } from "$app/state";
import { formatMediumDate } from "#lib/utils/formatting.js";

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

vi.mock("#lib/api/generated/linkedPlatforms.generated.remote.js", () => ({
	getLinkedPlatforms: () => ({
		get current() {
			return { platforms: [] };
		},
	}),
}));

vi.mock("#lib/api/generated/systems.generated.remote.js", () => ({
	getChannelStatuses: () => ({
		get current() {
			return statusCurrent;
		},
		get error() {
			return statusError;
		},
	}),
}));

vi.mock("#lib/api/generated/clientDevices.generated.remote.js", () => ({
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

const keepChannel = vi.fn();

vi.mock("#lib/api/generated/alertRules.generated.remote.js", () => ({
	getInAppRecipients: () => getInAppRecipients(),
	keepChannel: (args: unknown) => keepChannel(args),
}));

import ChannelsSection from "./ChannelsSection.svelte";
import { CHANNEL_META } from "./channelMeta";

/** Mirrors the server's UserSupplied destination modes. */
const REQUIRES_DESTINATION = new Set<ChannelType>([
	ChannelType.Webhook,
	ChannelType.DiscordChannel,
	ChannelType.SlackChannel,
	ChannelType.TelegramGroup,
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
			inApp({
				destination: departed,
				recipient: { subjectId: departed, canReceive: false },
				state: AlertChannelState.NoRecipient,
				memberName: "Alex",
			}),
		]);
		render(ChannelsSection, { channels, severity: AlertRuleSeverity.Warning });

		await expect
			.element(page.getByTestId("in-app-recipient-trigger"))
			.toHaveTextContent("Unavailable member");
		await expect
			.element(page.getByText("This channel has no recipient. Alex cannot receive it."))
			.toBeVisible();
		await expect.element(page.getByText("Saving the rule sends this channel to you.")).toBeVisible();
		expect(page.getByText(/has no recipient/i).elements()).toHaveLength(1);
	});

	it("warns about a stored channel that names no one instead of showing Me", async () => {
		const channels = $state<ChannelDef[]>([
			inApp({
				destination: "",
				recipient: { subjectId: undefined, canReceive: false },
				state: AlertChannelState.NoRecipient,
			}),
		]);
		render(ChannelsSection, { channels, severity: AlertRuleSeverity.Warning });

		await expect
			.element(page.getByTestId("in-app-recipient-trigger"))
			.toHaveTextContent("No recipient");
		await expect.element(page.getByText("This channel has no recipient.")).toBeVisible();
		expect(page.getByText(/has no recipient/i).elements()).toHaveLength(1);
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

	it("says only the state for a stored direct message that has no recipient", async () => {
		const channels = $state<ChannelDef[]>([
			{
				_uid: "slack-dm",
				channelType: ChannelType.SlackDm, state: AlertChannelState.NoRecipient,
				destination: "",
				destinationLabel: "",
			},
		]);
		render(ChannelsSection, { channels, severity: AlertRuleSeverity.Warning });

		await expect.element(page.getByText("This channel has no recipient.")).toBeVisible();
		expect(page.getByText(/account you linked/i).elements()).toHaveLength(0);
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

	describe("channel state", () => {
		const STOP = "2026-12-01T00:00:00Z";
		const date = formatMediumDate(STOP);
		const KEEP = { name: "Keep this channel" };

		function webhook(over: Partial<ChannelDef> = {}): ChannelDef {
			return {
				_uid: "webhook",
				id: "channel-1",
				channelType: ChannelType.Webhook,
				destination: "https://example.test/hook",
				destinationLabel: "",
				...over,
			};
		}

		async function showing(channel: ChannelDef, props: { ruleId?: string } = { ruleId: "rule-1" }) {
			const channels = $state<ChannelDef[]>([channel]);
			render(ChannelsSection, { channels, severity: AlertRuleSeverity.Warning, ...props });
			await expect.element(page.getByText("Webhook", { exact: true })).toBeVisible();
			return channels;
		}

		it("states a stopped channel with the member who added it", async () => {
			await showing(webhook({ state: AlertChannelState.Stopped, stopsAt: STOP, addedByName: "Alex" }));

			await expect
				.element(page.getByTestId("channel-state"))
				.toHaveTextContent(`This channel stopped on ${date}. Alex added it.`);
		});

		it("states a stopped channel when no one is known to have added it", async () => {
			await showing(webhook({ state: AlertChannelState.Stopped, stopsAt: STOP }));

			await expect
				.element(page.getByTestId("channel-state"))
				.toHaveTextContent(`This channel stopped on ${date}.`);
			expect(page.getByText(/added it/).elements()).toHaveLength(0);
		});

		it("states a channel with no recipient and the member it goes to", async () => {
			await showing(webhook({ state: AlertChannelState.NoRecipient, memberName: "Sam" }));

			await expect
				.element(page.getByTestId("channel-state"))
				.toHaveTextContent("This channel has no recipient. Sam cannot receive it.");
		});

		it("states a channel with no recipient and no known member", async () => {
			await showing(webhook({ state: AlertChannelState.NoRecipient }));

			await expect
				.element(page.getByTestId("channel-state"))
				.toHaveTextContent("This channel has no recipient.");
			expect(page.getByText(/cannot receive it/).elements()).toHaveLength(0);
		});

		it("states, quietly, the date an active channel stops", async () => {
			await showing(webhook({ state: AlertChannelState.Active, stopsAt: STOP, addedByName: "Alex" }));

			await expect
				.element(page.getByTestId("channel-state"))
				.toHaveTextContent(`This channel stops on ${date}. Alex added it.`);
			await expect.element(page.getByTestId("channel-state")).toHaveClass(/text-muted-foreground/);
			expect(page.getByRole("button", KEEP).elements()).toHaveLength(0);
		});

		it("states an active channel that stops without naming who added it", async () => {
			await showing(webhook({ state: AlertChannelState.Active, stopsAt: STOP }));

			await expect
				.element(page.getByTestId("channel-state"))
				.toHaveTextContent(`This channel stops on ${date}.`);
		});

		it("ties the line to its channel for a screen reader", async () => {
			await showing(webhook({ state: AlertChannelState.NoRecipient }));

			await expect
				.element(page.getByRole("group", { name: "Webhook" }))
				.toHaveAccessibleDescription("This channel has no recipient.");
		});

		it("states a stopped channel that has no stop date", async () => {
			await showing(webhook({ state: AlertChannelState.Stopped }));

			await expect.element(page.getByTestId("channel-state")).toHaveTextContent("This channel has stopped.");
		});

		it("states a state the web does not know as a channel that does not send", async () => {
			await showing(webhook({ state: "suspended" as AlertChannelState }));

			await expect.element(page.getByTestId("channel-state")).toHaveTextContent("This channel does not send.");
			expect(page.getByRole("button", KEEP).elements()).toHaveLength(0);
		});

		it("gives a stored channel the state line and no platform link hint", async () => {
			const channels = $state<ChannelDef[]>([
				{
					_uid: "tg",
					id: "channel-9",
					channelType: ChannelType.TelegramDm,
					destination: "-100",
					destinationLabel: "",
					state: AlertChannelState.NoRecipient,
				},
			]);
			render(ChannelsSection, { channels, severity: AlertRuleSeverity.Warning, ruleId: "rule-1" });

			await expect.element(page.getByTestId("channel-state")).toBeVisible();
			expect(page.getByText(/not linked/i).elements()).toHaveLength(0);
		});

		it("still says a new channel's platform is not linked", async () => {
			const channels = $state<ChannelDef[]>([
				{ _uid: "tg-new", channelType: ChannelType.TelegramDm, destination: "", destinationLabel: "" },
			]);
			render(ChannelsSection, { channels, severity: AlertRuleSeverity.Warning });

			await expect.element(page.getByText(/Telegram not linked/)).toBeVisible();
		});

		it("states nothing for a channel with no state", async () => {
			await showing(webhook());

			expect(page.getByTestId("channel-state").elements()).toHaveLength(0);
			expect(page.getByRole("button", KEEP).elements()).toHaveLength(0);
		});

		it("states nothing for an active channel that does not stop", async () => {
			await showing(webhook({ state: AlertChannelState.Active }));

			expect(page.getByTestId("channel-state").elements()).toHaveLength(0);
		});

		it("offers to keep a stopped channel, and shows what the API answers", async () => {
			keepChannel.mockResolvedValue({
				channels: [
					{
						id: "channel-1",
						state: AlertChannelState.Active,
						stopsAt: "2027-03-01T00:00:00Z",
						addedByName: "Jo",
					},
				],
			});
			const channels = await showing(
				webhook({ state: AlertChannelState.Stopped, stopsAt: STOP, addedByName: "Alex" }),
			);

			await page.getByRole("button", KEEP).click();

			await expect
				.element(page.getByTestId("channel-state"))
				.toHaveTextContent(
					`This channel stops on ${formatMediumDate("2027-03-01T00:00:00Z")}. Jo added it.`,
				);
			expect(keepChannel).toHaveBeenCalledWith({ ruleId: "rule-1", channelId: "channel-1" });
			expect(channels[0].state).toBe(AlertChannelState.Active);
			expect(page.getByRole("button", KEEP).elements()).toHaveLength(0);
		});

		it("says when keeping a channel failed and leaves it stopped", async () => {
			keepChannel.mockRejectedValue(new Error("Only a member can keep a channel."));
			await showing(webhook({ state: AlertChannelState.Stopped, stopsAt: STOP }));

			await page.getByRole("button", KEEP).click();

			await expect.element(page.getByRole("alert")).toBeVisible();
			await expect.element(page.getByRole("button", KEEP)).toBeVisible();
		});

		it("does not offer to keep a channel to someone who cannot edit the rule", async () => {
			pageState.data = { effectivePermissions: ["alerts.read"] } as unknown as typeof pageState.data;
			await showing(webhook({ state: AlertChannelState.Stopped, stopsAt: STOP }));

			await expect.element(page.getByTestId("channel-state")).toBeVisible();
			expect(page.getByRole("button", KEEP).elements()).toHaveLength(0);
		});

		it("does not offer to keep a channel on a rule that is not stored", async () => {
			await showing(webhook({ state: AlertChannelState.Stopped, stopsAt: STOP }), {});

			await expect.element(page.getByTestId("channel-state")).toBeVisible();
			expect(page.getByRole("button", KEEP).elements()).toHaveLength(0);
		});

		it("does not offer to keep a channel that goes to a member", async () => {
			const departed = "0199a1b2-0000-7000-8000-00000000000c";
			const channels = $state<ChannelDef[]>([
				inApp({
					id: "channel-2",
					destination: departed,
					recipient: { subjectId: departed, canReceive: false },
					state: AlertChannelState.Stopped,
					stopsAt: STOP,
				}),
			]);
			render(ChannelsSection, { channels, severity: AlertRuleSeverity.Warning, ruleId: "rule-1" });

			await expect.element(page.getByTestId("channel-state")).toBeVisible();
			expect(page.getByRole("button", KEEP).elements()).toHaveLength(0);
		});
	});
});
