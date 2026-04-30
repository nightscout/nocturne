import { describe, it, expect } from "vitest";
import {
	defaultSchedule,
	defaultClientConfig,
	defaultPayload,
	nodeFromApi,
	nodeToApi,
	parseRule,
} from "./types";

describe("defaultSchedule", () => {
	it("returns a valid default schedule", () => {
		const schedule = defaultSchedule();

		expect(schedule.name).toBe("Default Schedule");
		expect(schedule.isDefault).toBe(true);
		expect(schedule.daysOfWeek).toEqual([]);
		expect(schedule.startTime).toBe("00:00");
		expect(schedule.endTime).toBe("23:59");
		expect(schedule.timezone).toBe("UTC");
		expect(schedule.expanded).toBe(true);
		expect(schedule.escalationSteps).toHaveLength(1);
		expect(schedule.escalationSteps[0].stepOrder).toBe(0);
		expect(schedule.escalationSteps[0].delaySeconds).toBe(0);
		expect(schedule.escalationSteps[0].channels).toHaveLength(1);
	});
});

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
		expect(node).toEqual({
			type: "threshold",
			threshold: { direction: "below", value: 70 },
		});
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
		expect(state.condition?.type).toBe("threshold");
		expect(state.autoResolveEnabled).toBe(false);
		expect(state.autoResolveCondition).toBeNull();
		expect(state.schedules).toHaveLength(1);
		expect(state.schedules[0].name).toBe("Default Schedule");
	});

	it("parses a threshold rule into a ConditionNode", () => {
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
			schedules: [],
		} as never);

		expect(state.name).toBe("Low Alert");
		expect(state.description).toBe("Alert when glucose is low");
		expect(state.condition?.type).toBe("threshold");
		expect(state.condition?.threshold?.direction).toBe("below");
		expect(state.condition?.threshold?.value).toBe(70);
	});

	it("parses auto-resolve params", () => {
		const state = parseRule({
			name: "Test",
			conditionType: "threshold",
			conditionParams: { direction: "below", value: 70 },
			autoResolveEnabled: true,
			autoResolveParams: {
				operator: "and",
				conditions: [
					{ type: "threshold", threshold: { direction: "above", value: 80 } },
				],
			},
			schedules: [],
		} as never);

		expect(state.autoResolveEnabled).toBe(true);
		expect(state.autoResolveCondition?.type).toBe("composite");
	});

	it("parses schedules with escalation steps", () => {
		const state = parseRule({
			name: "Test",
			conditionType: "threshold",
			conditionParams: { direction: "below", value: 70 },
			schedules: [
				{
					name: "Work Hours",
					isDefault: false,
					daysOfWeek: [1, 2, 3, 4, 5],
					startTime: "09:00",
					endTime: "17:00",
					timezone: "America/New_York",
					escalationSteps: [
						{
							stepOrder: 0,
							delaySeconds: 0,
							channels: [
								{
									channelType: "WebPush",
									destination: "",
									destinationLabel: "",
								},
							],
						},
						{
							stepOrder: 1,
							delaySeconds: 300,
							channels: [],
						},
					],
				},
			],
		} as never);

		expect(state.schedules).toHaveLength(1);
		expect(state.schedules[0].name).toBe("Work Hours");
		expect(state.schedules[0].daysOfWeek).toEqual([1, 2, 3, 4, 5]);
		expect(state.schedules[0].startTime).toBe("09:00");
		expect(state.schedules[0].escalationSteps).toHaveLength(2);
		expect(state.schedules[0].escalationSteps[1].delaySeconds).toBe(300);
	});

	it("uses defaults for missing client configuration", () => {
		const state = parseRule({
			name: "Test",
			conditionType: "threshold",
			conditionParams: { direction: "below", value: 70 },
			clientConfiguration: undefined,
			schedules: [],
		} as never);

		expect(state.clientConfig.audio.enabled).toBe(true);
		expect(state.clientConfig.audio.sound).toBe("alarm-default");
		expect(state.clientConfig.visual.flashEnabled).toBe(false);
		expect(state.clientConfig.snooze.defaultMinutes).toBe(15);
	});

	it("sorts escalation steps by stepOrder", () => {
		const state = parseRule({
			name: "Test",
			conditionType: "threshold",
			conditionParams: { direction: "below", value: 70 },
			schedules: [
				{
					name: "Default",
					escalationSteps: [
						{ stepOrder: 2, delaySeconds: 600, channels: [] },
						{ stepOrder: 0, delaySeconds: 0, channels: [] },
						{ stepOrder: 1, delaySeconds: 300, channels: [] },
					],
				},
			],
		} as never);

		const steps = state.schedules[0].escalationSteps;
		expect(steps[0].stepOrder).toBe(0);
		expect(steps[1].stepOrder).toBe(1);
		expect(steps[2].stepOrder).toBe(2);
	});
});
