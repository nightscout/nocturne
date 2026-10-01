import { describe, it, expect, vi, afterEach } from "vitest";
import {
	browserTimeZone,
	defaultClientConfig,
	defaultPayload,
	nodeFromApi,
	nodeToApi,
	parseRule,
	applyChannelDestination,
	buildBody,
	parseChannelMetadata,
	validateChannels,
	type ChannelDef,
	type ConditionNode,
} from "./types";
import { AlertConditionType, AlertRuleSeverity, ChannelType } from "$api-clients";
import type { AlertRuleResponse } from "$api-clients";

describe("defaultClientConfig", () => {
	it("returns valid audio defaults", () => {
		const config = defaultClientConfig();

		expect(config.audio.enabled).toBe(true);
		expect(config.audio.sound).toBe("alarm-default");
		expect(config.audio.customSoundId).toBeNull();
		expect(config.audio.ascending).toBe(false);
		expect(config.audio.startVolume).toBe(50);
		expect(config.audio.maxVolume).toBe(80);
		expect(config.audio.repeatCount).toBe(2);
	});

	it("returns valid visual defaults", () => {
		const config = defaultClientConfig();

		expect(config.visual.flashEnabled).toBe(false);
		expect(config.visual.flashColor).toBe("#ff0000");
		expect(config.visual.persistentBanner).toBe(true);
		expect(config.visual.wakeScreen).toBe(false);
	});

	it("returns valid snooze defaults", () => {
		const config = defaultClientConfig();

		expect(config.snooze.defaultMinutes).toBe(15);
		expect(config.snooze.options).toEqual([5, 15, 30, 60]);
		// Mirrors SmartSnoozeConfig.DefaultMaxCount / DefaultExtendMinutes on the backend.
		expect(config.snooze.maxCount).toBe(3);
		expect(config.snooze.smartSnooze).toBe(false);
		expect(config.snooze.smartSnoozeExtendMinutes).toBe(10);
	});
});

describe("defaultPayload", () => {
	it("returns a threshold node by default", () => {
		const node = defaultPayload("threshold");
		expect(node.type).toBe("threshold");
		expect(node.threshold).toEqual({ direction: "below", value: 70 });
	});

	it("composite default has a single threshold child", () => {
		const node = defaultPayload("composite");
		expect(node.composite?.operator).toBe("and");
		expect(node.composite?.conditions).toHaveLength(1);
		expect(node.composite?.conditions[0].type).toBe("threshold");
	});

	it("time_of_day default carries the browser's resolved IANA timezone", () => {
		// The user picks "10am-2pm" on their device — they mean local wall-clock. Saving
		// the rule without a timezone made the backend interpret the window as UTC, firing
		// at the wrong hour for non-UTC users. The editor now stamps the browser tz at
		// creation time so the rule JSON is self-documenting on the wire.
		const node = defaultPayload("time_of_day");
		const expected = Intl.DateTimeFormat().resolvedOptions().timeZone;
		expect(node.time_of_day?.timezone).toBe(expected);
		expect(expected).toBeTruthy();
	});

	it("tracker_age default has an empty definition id, >= operator, and 0 minutes", () => {
		const node = defaultPayload("tracker_age");
		expect(node.type).toBe("tracker_age");
		expect(node.tracker_age).toEqual({
			tracker_definition_id: "",
			operator: ">=",
			minutes: 0,
		});
	});
});

describe("nodeFromApi / nodeToApi", () => {
	it("wraps API kind + payload into a ConditionNode", () => {
		const node = nodeFromApi("threshold", { direction: "below", value: 70 });
		// nodeFromApi stamps _uid for editor-side React-keying; assert ignoring it.
		expect(node).toMatchObject({
			type: "threshold",
			threshold: { direction: "below", value: 70 },
		});
		expect(node?._uid).toBeDefined();
	});

	it("returns null when kind or params are missing", () => {
		expect(nodeFromApi(undefined, {})).toBeNull();
		expect(nodeFromApi("threshold", null)).toBeNull();
	});

	it("nodeToApi extracts the kind-specific payload", () => {
		const result = nodeToApi({
			type: "threshold",
			threshold: { direction: "above", value: 180 },
		});
		expect(result).toEqual({
			conditionType: "threshold",
			conditionParams: { direction: "above", value: 180 },
		});
	});
});

describe("parseRule", () => {
	it("returns default state when passed null", () => {
		const state = parseRule(null);

		expect(state.name).toBe("");
		expect(state.description).toBe("");
		expect(state.isEnabled).toBe(true);
		// parseRule wraps non-composite roots in a single-child AND group so the
		// inline rule builder always edits at the group level.
		expect(state.condition?.type).toBe("composite");
		expect(state.condition?.composite?.operator).toBe("and");
		expect(state.condition?.composite?.conditions[0].type).toBe("threshold");
		expect(state.autoResolveEnabled).toBe(false);
		expect(state.autoResolveCondition).toBeNull();
	});

	it("wraps a leaf-rooted rule in a single-child AND group", () => {
		const state = parseRule({
			name: "Low Alert",
			description: "Alert when glucose is low",
			severity: AlertRuleSeverity.Warning,
			conditionType: AlertConditionType.Threshold,
			conditionParams: {
				direction: "below",
				value: 70,
			},
			isEnabled: true,
			sortOrder: 1,
		});

		expect(state.name).toBe("Low Alert");
		expect(state.condition?.type).toBe("composite");
		const inner = state.condition?.composite?.conditions[0];
		expect(inner?.type).toBe("threshold");
		expect(inner?.threshold?.direction).toBe("below");
		expect(inner?.threshold?.value).toBe(70);
	});

	it("leaves a composite-rooted rule untouched", () => {
		const state = parseRule({
			name: "Combo",
			conditionType: AlertConditionType.Composite,
			conditionParams: {
				operator: "or",
				conditions: [
					{ type: "threshold", threshold: { direction: "below", value: 70 } },
					{ type: "trend", trend: { bucket: "falling_fast" } },
				],
			},
		});

		expect(state.condition?.type).toBe("composite");
		expect(state.condition?.composite?.operator).toBe("or");
		expect(state.condition?.composite?.conditions).toHaveLength(2);
	});

	it("fills snooze fields a stored rule omits with the backend's defaults", () => {
		const state = parseRule({
			name: "Low Alert",
			conditionType: "threshold",
			conditionParams: { direction: "below", value: 70 },
			clientConfiguration: { snooze: { smartSnooze: true } },
		} as never);

		expect(state.clientConfig.snooze.smartSnooze).toBe(true);
		expect(state.clientConfig.snooze.maxCount).toBe(3);
		expect(state.clientConfig.snooze.smartSnoozeExtendMinutes).toBe(10);
	});

	it("stamps a distinct _uid on every node of a reloaded snooze group", () => {
		const state = parseRule({
			name: "Snooze",
			conditionType: AlertConditionType.Threshold,
			conditionParams: { direction: "below", value: 70 },
			clientConfiguration: {
				snooze: {
					conditions: [
						{
							type: "composite",
							composite: {
								operator: "and",
								conditions: [
									{
										type: "threshold",
										threshold: { direction: "below", value: 70 },
									},
									{ type: "trend", trend: { bucket: "falling" } },
								],
							},
						},
					],
				},
			},
		} as never);

		const nodes: ConditionNode[] = [];
		const walk = (node: ConditionNode) => {
			nodes.push(node);
			for (const child of node.composite?.conditions ?? []) walk(child);
			if (node.not?.child) walk(node.not.child);
			if (node.sustained?.child) walk(node.sustained.child);
		};
		for (const root of state.clientConfig.snooze.conditions) walk(root);

		expect(nodes).toHaveLength(3);
		expect(
			nodes.every((n) => typeof n._uid === "string" && n._uid.length > 0)
		).toBe(true);
		expect(new Set(nodes.map((n) => n._uid)).size).toBe(nodes.length);
	});

	it("parses auto-resolve params from a full ConditionNode envelope", () => {
		// The backend stores autoResolveParams as a self-describing envelope
		// (the wire shape includes the `type` discriminator alongside the
		// kind's payload field).
		const state = parseRule({
			name: "Test",
			conditionType: AlertConditionType.Threshold,
			conditionParams: { direction: "below", value: 70 },
			autoResolveEnabled: true,
			autoResolveParams: {
				type: "composite",
				composite: {
					operator: "and",
					conditions: [
						{ type: "threshold", threshold: { direction: "above", value: 80 } },
					],
				},
			},
		});

		expect(state.autoResolveEnabled).toBe(true);
		expect(state.autoResolveCondition?.type).toBe("composite");
	});

	it("wraps a leaf-rooted auto-resolve envelope in a single-child AND group", () => {
		const state = parseRule({
			name: "Test",
			conditionType: AlertConditionType.Threshold,
			conditionParams: { direction: "below", value: 70 },
			autoResolveEnabled: true,
			autoResolveParams: {
				type: "threshold",
				threshold: { direction: "above", value: 80 },
			},
		});

		expect(state.autoResolveCondition?.type).toBe("composite");
		expect(state.autoResolveCondition?.composite?.conditions[0].type).toBe(
			"threshold",
		);
	});

	it("parses the flat channel list and the allow-through-DND flag", () => {
		const state = parseRule({
			name: "Low Alert",
			conditionType: AlertConditionType.Threshold,
			conditionParams: { direction: "below", value: 70 },
			allowThroughDnd: true,
			channels: [
				{
					id: "11111111-1111-1111-1111-111111111111",
					channelType: ChannelType.DiscordDm,
					destination: "https://discord/webhook/x",
					destinationLabel: "Family channel",
					sortOrder: 1,
				},
				{
					id: "22222222-2222-2222-2222-222222222222",
					channelType: ChannelType.WebPush,
					destination: "",
					destinationLabel: undefined,
					sortOrder: 0,
				},
			],
		});

		expect(state.allowThroughDnd).toBe(true);
		expect(state.channels).toHaveLength(2);
		// Sorted by sortOrder, so WebPush (0) comes before Discord (1).
		expect(state.channels[0].channelType).toBe("web_push");
		expect(state.channels[1].channelType).toBe("discord_dm");
		expect(state.channels[1].destinationLabel).toBe("Family channel");
	});

	it("falls back to a default channel list when the API returns none", () => {
		const state = parseRule({
			name: "Test",
			conditionType: AlertConditionType.Threshold,
			conditionParams: { direction: "below", value: 70 },
		});

		expect(state.channels).toHaveLength(1);
		expect(state.channels[0].channelType).toBe("web_push");
		expect(state.allowThroughDnd).toBe(false);
	});

	it("uses defaults for missing client configuration", () => {
		const state = parseRule({
			name: "Test",
			conditionType: AlertConditionType.Threshold,
			conditionParams: { direction: "below", value: 70 },
			clientConfiguration: undefined,
		});

		expect(state.clientConfig.audio.enabled).toBe(true);
		expect(state.clientConfig.audio.sound).toBe("alarm-default");
		expect(state.clientConfig.visual.flashEnabled).toBe(false);
		expect(state.clientConfig.snooze.defaultMinutes).toBe(15);
	});

});

describe("applyChannelDestination", () => {
	function webhookChannel(over: Partial<ChannelDef> = {}): ChannelDef {
		return {
			channelType: ChannelType.Webhook,
			destination: "https://receiver.example.com/hook",
			destinationLabel: "",
			...over,
		};
	}

	it("stops reporting a saved secret once the destination is edited", () => {
		const channel = webhookChannel({ hasSecret: true });
		applyChannelDestination(channel, "https://elsewhere.example.com/hook");
		expect(channel.hasSecret).toBe(false);
		expect(channel.destination).toBe("https://elsewhere.example.com/hook");
	});

	it("keeps reporting a saved secret while the destination is unchanged", () => {
		const channel = webhookChannel({ hasSecret: true });
		applyChannelDestination(channel, channel.destination);
		expect(channel.hasSecret).toBe(true);
	});

	it("leaves the indicator alone when a replacement secret has been typed", () => {
		const channel = webhookChannel({ hasSecret: true, secret: "typed" });
		applyChannelDestination(channel, "https://elsewhere.example.com/hook");
		expect(channel.hasSecret).toBe(true);
	});

	it("sends no secret for the new destination after the URL is edited", () => {
		const state = parseRule(null);
		state.channels = [webhookChannel({ hasSecret: true })];
		applyChannelDestination(state.channels[0], "https://elsewhere.example.com/hook");
		expect(
			buildBody(state).channels[0].secret
		).toBe("");
	});
});

describe("buildBody webhook secret", () => {
	function webhookState(over: Partial<ChannelDef>) {
		const state = parseRule(null);
		state.channels = [
			{
				channelType: ChannelType.Webhook,
				destination: "https://receiver.example.com/hook",
				destinationLabel: "",
				...over,
			},
		];
		return state;
	}

	function sentSecret(over: Partial<ChannelDef>) {
		return buildBody(webhookState(over)).channels[0].secret;
	}

	it("omits the secret when the channel already has one, so the save keeps it", () => {
		expect(sentSecret({ hasSecret: true })).toBeUndefined();
	});

	it("sends an empty secret once the editor has removed the stored one", () => {
		expect(sentSecret({ hasSecret: false })).toBe("");
	});

	it("sends a typed secret over the stored one", () => {
		expect(sentSecret({ hasSecret: true, secret: "typed" })).toBe("typed");
	});
});

describe("buildBody", () => {
	it("produces no _uid fields in any part of the output", () => {
		const state = parseRule(null);
		const body = buildBody(state);
		const json = JSON.stringify(body);
		expect(json).not.toContain("_uid");
	});

	it("sends a group-rooted rule's conditions without _uid fields", () => {
		const state = parseRule(null);
		state.condition = defaultPayload("composite");
		state.condition.composite?.conditions.push(defaultPayload("iob"));
		expect(JSON.stringify(buildBody(state).conditionParams)).not.toContain("_uid");
	});

	it("two semantically-identical states with different _uids produce the same JSON", () => {
		// parseRule stamps fresh _uids on every call, so two invocations with the
		// same input will have different internal identities.
		const state1 = parseRule({
			name: "Low Alert",
			conditionType: AlertConditionType.Threshold,
			conditionParams: { direction: "below", value: 70 },
		});
		const state2 = parseRule({
			name: "Low Alert",
			conditionType: AlertConditionType.Threshold,
			conditionParams: { direction: "below", value: 70 },
		});
		expect(JSON.stringify(buildBody(state1))).toBe(JSON.stringify(buildBody(state2)));
	});

	it("flattens a single-child composite root to a leaf", () => {
		// parseRule always wraps leaf conditions in a single-child AND group;
		// buildBody must flatten it back before sending.
		const state = parseRule({
			name: "Test",
			conditionType: AlertConditionType.Threshold,
			conditionParams: { direction: "above", value: 180 },
		});
		const body = buildBody(state);
		expect(body.conditionType).toBe("threshold");
		expect(body.conditionParams).toEqual({ direction: "above", value: 180 });
	});

	it("converts empty description to undefined", () => {
		const state = parseRule(null); // description defaults to ""
		const body = buildBody(state);
		expect(body.description).toBeUndefined();
	});

	it("sets autoResolveParams to undefined when autoResolveCondition is null", () => {
		const state = parseRule(null);
		expect(state.autoResolveCondition).toBeNull();
		const body = buildBody(state);
		expect(body.autoResolveParams).toBeUndefined();
	});

	it("strips _uid from channels", () => {
		const state = parseRule({
			name: "Test",
			conditionType: AlertConditionType.Threshold,
			conditionParams: { direction: "below", value: 70 },
			channels: [
				{ channelType: ChannelType.WebPush, destination: "", sortOrder: 0 },
			],
		});
		const body = buildBody(state);
		const json = JSON.stringify(body.channels);
		expect(json).not.toContain("_uid");
	});

	it("serialises a device_action channel as {channelType, destination, metadata}", () => {
		const state = parseRule({
			name: "Test",
			conditionType: AlertConditionType.Threshold,
			conditionParams: { direction: "below", value: 70 },
			channels: [
				{
					channelType: ChannelType.DeviceAction,
					destination: "companion",
					metadata: { capabilities: ["notify", "tray_flash"] },
					sortOrder: 0,
				},
			],
		});
		const body = buildBody(state);
		const ch = body.channels[0];
		expect(ch.channelType).toBe("device_action");
		expect(ch.destination).toBe("companion");
		// Metadata is a JSON object (not a pre-stringified string) — the server
		// serialises it to JSONB.
		expect(ch.metadata).toEqual({ capabilities: ["notify", "tray_flash"] });
	});

	it("drops a nested group left with no conditions, and any wrapper around it", () => {
		const state = parseRule(null);
		state.condition = {
			type: "composite",
			composite: {
				operator: "and",
				conditions: [
					defaultPayload("threshold"),
					{ type: "composite", composite: { operator: "or", conditions: [] } },
					{
						type: "not",
						not: { child: { type: "composite", composite: { operator: "and", conditions: [] } } },
					},
				],
			},
		};
		const body = buildBody(state);
		expect(body.conditionType).toBe("threshold");
		expect(body.conditionParams).toEqual({ direction: "below", value: 70 });
	});

	it("keeps an empty root group so saving reports it", () => {
		const state = parseRule(null);
		state.condition = { type: "composite", composite: { operator: "and", conditions: [] } };
		const body = buildBody(state);
		expect(body.conditionType).toBe("composite");
		expect(body.conditionParams).toEqual({ operator: "and", conditions: [] });
	});

	it("drops empty groups from auto-resolve and snooze conditions", () => {
		const state = parseRule(null);
		const empty: ConditionNode = { type: "composite", composite: { operator: "or", conditions: [] } };
		state.autoResolveCondition = {
			type: "composite",
			composite: { operator: "and", conditions: [defaultPayload("iob"), empty] },
		};
		state.clientConfig.snooze.conditions = [empty, defaultPayload("trend")];
		const body = buildBody(state);
		expect(body.autoResolveParams).toEqual({ type: "iob", iob: { operator: ">=", value: 1 } });
		expect(body.clientConfiguration.snooze.conditions).toEqual([
			{ type: "trend", trend: { bucket: "falling" } },
		]);
	});

	it("omits metadata for channels without it", () => {
		const state = parseRule({
			name: "Test",
			conditionType: AlertConditionType.Threshold,
			conditionParams: { direction: "below", value: 70 },
			channels: [{ channelType: ChannelType.WebPush, destination: "", sortOrder: 0 }],
		});
		const body = buildBody(state);
		expect(body.channels[0].metadata).toBeUndefined();
	});
});

describe("parseChannelMetadata", () => {
	it("returns null for null/undefined", () => {
		expect(parseChannelMetadata(null)).toBeNull();
		expect(parseChannelMetadata(undefined)).toBeNull();
	});

	it("reads capabilities from a deserialised object", () => {
		expect(parseChannelMetadata({ capabilities: ["notify", "torch"] })).toEqual({
			capabilities: ["notify", "torch"],
		});
	});

	it("parses a JSON string form defensively", () => {
		expect(
			parseChannelMetadata('{"capabilities":["notify"]}'),
		).toEqual({ capabilities: ["notify"] });
	});

	it("returns null for malformed input", () => {
		expect(parseChannelMetadata("not json")).toBeNull();
		expect(parseChannelMetadata({ capabilities: "nope" })).toBeNull();
	});

	it("drops non-string capability entries", () => {
		expect(
			parseChannelMetadata({ capabilities: ["notify", 5, null, "torch"] }),
		).toEqual({ capabilities: ["notify", "torch"] });
	});

	it("round-trips a device_action channel through parseRule", () => {
		const state = parseRule({
			name: "Test",
			conditionType: AlertConditionType.Threshold,
			conditionParams: { direction: "below", value: 70 },
			channels: [
				{
					channelType: ChannelType.DeviceAction,
					destination: "companion",
					metadata: { capabilities: ["notify"] },
					sortOrder: 0,
				},
			],
		});
		const device = state.channels.find(
			(c) => c.channelType === "device_action",
		);
		expect(device?.destination).toBe("companion");
		expect(device?.metadata).toEqual({ capabilities: ["notify"] });
	});
});

describe("validateChannels", () => {
	function channel(over: Partial<ChannelDef> = {}): ChannelDef {
		return {
			channelType: ChannelType.WebPush,
			destination: "",
			destinationLabel: "",
			...over,
		};
	}

	it("rejects a device_action channel with an empty destination", () => {
		const result = validateChannels([
			channel({ channelType: ChannelType.DeviceAction, destination: "" }),
		]);
		expect(result).toMatch(/device kind/i);
	});

	it("accepts a device_action channel with a kind selected", () => {
		expect(
			validateChannels([
				channel({
					channelType: ChannelType.DeviceAction,
					destination: "companion",
					metadata: { capabilities: ["notify"] },
				}),
			]),
		).toBeNull();
	});

	it("does not require a destination for non-device channels", () => {
		expect(
			validateChannels([
				channel({ channelType: ChannelType.WebPush, destination: "" }),
				channel({ channelType: ChannelType.InApp, destination: "" }),
			]),
		).toBeNull();
	});

	it("accepts an empty channel list", () => {
		expect(validateChannels([])).toBeNull();
	});
});

describe("browserTimeZone", () => {
	afterEach(() => vi.restoreAllMocks());

	const reporting = (timeZone: string) =>
		vi.spyOn(Intl.DateTimeFormat.prototype, "resolvedOptions").mockReturnValue({
			...new Intl.DateTimeFormat().resolvedOptions(),
			timeZone,
		});

	it("is the zone the browser reports", () => {
		reporting("Europe/London");
		expect(browserTimeZone()).toBe("Europe/London");
	});

	it("is undefined when the browser cannot tell, so the rule uses the tenant's zone", () => {
		reporting("Etc/Unknown");
		expect(browserTimeZone()).toBeUndefined();
		expect(defaultPayload("time_of_day").time_of_day?.timezone).toBeUndefined();
	});

	it("is undefined for an id Intl rejects", () => {
		reporting("Not/AZone");
		expect(browserTimeZone()).toBeUndefined();
	});
});

describe("reading a stored rule", () => {
	const stored = (overrides: Partial<AlertRuleResponse>): AlertRuleResponse => ({
		name: "Stored",
		conditionType: AlertConditionType.Threshold,
		conditionParams: { direction: "below", value: 70 },
		...overrides,
	});

	it("writes the values the editor shows for fields the rule leaves out", () => {
		const state = parseRule(
			stored({
				conditionType: AlertConditionType.Composite,
				conditionParams: {
					operator: "and",
					conditions: [
						{ type: "iob", iob: { value: 2 } },
						{ type: "not", not: { child: { type: "threshold", threshold: { value: 70 } } } },
						{ type: "time_of_day", time_of_day: { timezone: "Europe/London" } },
					],
				},
			}),
		);

		expect(buildBody(state).conditionParams).toEqual({
			operator: "and",
			conditions: [
				{ type: "iob", iob: { operator: ">=", value: 2 } },
				{ type: "not", not: { child: { type: "threshold", threshold: { direction: "below", value: 70 } } } },
				{ type: "time_of_day", time_of_day: { from: "00:00", to: "23:59", timezone: "Europe/London" } },
			],
		});
	});

	it("fills auto-resolve and snooze leaves too", () => {
		const state = parseRule(
			stored({
				autoResolveEnabled: true,
				autoResolveParams: { type: "staleness", staleness: { value: 20 } },
				clientConfiguration: {
					snooze: { smartSnooze: true, conditions: [{ type: "pump_suspended", pump_suspended: {} }] },
				},
			}),
		);
		const body = buildBody(state);

		expect(body.autoResolveParams).toEqual({ type: "staleness", staleness: { operator: ">=", value: 20 } });
		expect(body.clientConfiguration.snooze.conditions).toEqual([
			{ type: "pump_suspended", pump_suspended: { is_active: false } },
		]);
	});

	it("saves a group with no list or child without throwing, keeping a root one for the save to report", () => {
		const state = parseRule(
			stored({
				conditionType: AlertConditionType.Composite,
				conditionParams: {
					operator: "and",
					conditions: [
						{ type: "threshold", threshold: { direction: "below", value: 70 } },
						{ type: "composite", composite: { operator: "or" } },
						{ type: "not", not: {} },
						null,
					],
				},
				autoResolveEnabled: true,
				autoResolveParams: { type: "composite", composite: { operator: "and" } },
			}),
		);
		const body = buildBody(state);

		expect(body.conditionParams).toEqual({ direction: "below", value: 70 });
		expect(body.autoResolveParams).toEqual({ type: "composite", composite: { operator: "and" } });
	});
});
