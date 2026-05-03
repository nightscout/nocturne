import { describe, it, expect } from "vitest";
import {
	defaultClientConfig,
	defaultPayload,
	nodeFromApi,
	nodeToApi,
	parseRule,
} from "./types";

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
		expect(config.snooze.maxCount).toBe(5);
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
			severity: "warning",
			conditionType: "threshold",
			conditionParams: {
				direction: "below",
				value: 70,
			},
			isEnabled: true,
			sortOrder: 1,
		} as never);

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
			conditionType: "composite",
			conditionParams: {
				operator: "or",
				conditions: [
					{ type: "threshold", threshold: { direction: "below", value: 70 } },
					{ type: "trend", trend: { bucket: "falling_fast" } },
				],
			},
		} as never);

		expect(state.condition?.type).toBe("composite");
		expect(state.condition?.composite?.operator).toBe("or");
		expect(state.condition?.composite?.conditions).toHaveLength(2);
	});

	it("parses auto-resolve params from a full ConditionNode envelope", () => {
		// The backend stores autoResolveParams as a self-describing envelope
		// (the wire shape includes the `type` discriminator alongside the
		// kind's payload field).
		const state = parseRule({
			name: "Test",
			conditionType: "threshold",
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
		} as never);

		expect(state.autoResolveEnabled).toBe(true);
		expect(state.autoResolveCondition?.type).toBe("composite");
	});

	it("wraps a leaf-rooted auto-resolve envelope in a single-child AND group", () => {
		const state = parseRule({
			name: "Test",
			conditionType: "threshold",
			conditionParams: { direction: "below", value: 70 },
			autoResolveEnabled: true,
			autoResolveParams: {
				type: "threshold",
				threshold: { direction: "above", value: 80 },
			},
		} as never);

		expect(state.autoResolveCondition?.type).toBe("composite");
		expect(state.autoResolveCondition?.composite?.conditions[0].type).toBe(
			"threshold",
		);
	});

	it("parses the flat channel list and the allow-through-DND flag", () => {
		const state = parseRule({
			name: "Low Alert",
			conditionType: "threshold",
			conditionParams: { direction: "below", value: 70 },
			allowThroughDnd: true,
			channels: [
				{
					id: "11111111-1111-1111-1111-111111111111",
					channelType: "discord_dm",
					destination: "https://discord/webhook/x",
					destinationLabel: "Family channel",
					sortOrder: 1,
				},
				{
					id: "22222222-2222-2222-2222-222222222222",
					channelType: "web_push",
					destination: "",
					destinationLabel: null,
					sortOrder: 0,
				},
			],
		} as never);

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
			conditionType: "threshold",
			conditionParams: { direction: "below", value: 70 },
		} as never);

		expect(state.channels).toHaveLength(1);
		expect(state.channels[0].channelType).toBe("web_push");
		expect(state.allowThroughDnd).toBe(false);
	});

	it("uses defaults for missing client configuration", () => {
		const state = parseRule({
			name: "Test",
			conditionType: "threshold",
			conditionParams: { direction: "below", value: 70 },
			clientConfiguration: undefined,
		} as never);

		expect(state.clientConfig.audio.enabled).toBe(true);
		expect(state.clientConfig.audio.sound).toBe("alarm-default");
		expect(state.clientConfig.visual.flashEnabled).toBe(false);
		expect(state.clientConfig.snooze.defaultMinutes).toBe(15);
	});

});
