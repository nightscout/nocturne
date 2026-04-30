import { AlertRuleSeverity, ChannelType } from "$api-clients";
import type { AlertRuleResponse } from "$api-clients";

// ---------------------------------------------------------------------------
// Recursive ConditionNode shape
// ---------------------------------------------------------------------------
//
// The API stores each condition as `{ conditionType, conditionParams }` where
// `conditionParams` is the kind-specific payload only. The frontend wraps
// payloads in a `ConditionNode` that carries the kind alongside the payload
// so a single recursive editor component can manipulate any node.

export type ConditionKind =
	| "composite"
	| "not"
	| "sustained"
	| "threshold"
	| "rate_of_change"
	| "staleness"
	| "predicted"
	| "trend"
	| "time_of_day"
	| "iob"
	| "cob"
	| "reservoir"
	| "site_age"
	| "sensor_age"
	| "alert_state";

export type ComparisonOperator = ">=" | ">" | "<=" | "<";

export interface CompositePayload {
	operator: "and" | "or";
	conditions: ConditionNode[];
}

export interface NotPayload {
	child: ConditionNode;
}

export interface SustainedPayload {
	minutes: number;
	child: ConditionNode;
}

export interface ThresholdPayload {
	direction: "above" | "below";
	value: number;
}

export interface RateOfChangePayload {
	direction: "rising" | "falling";
	rate: number;
}

export interface StalenessPayload {
	operator: ComparisonOperator;
	value: number;
}

export interface PredictedPayload {
	operator: ComparisonOperator;
	value: number;
	withinMinutes: number;
}

export type TrendBucket =
	| "falling_fast"
	| "falling"
	| "flat"
	| "rising"
	| "rising_fast";

export interface TrendPayload {
	bucket: TrendBucket;
}

export interface TimeOfDayPayload {
	from: string;
	to: string;
	timezone?: string;
}

export interface IobPayload {
	operator: ComparisonOperator;
	value: number;
}

export interface CobPayload {
	operator: ComparisonOperator;
	value: number;
}

export interface ReservoirPayload {
	operator: ComparisonOperator;
	value: number;
}

export interface SiteAgePayload {
	operator: ComparisonOperator;
	value: number;
}

export interface SensorAgePayload {
	operator: ComparisonOperator;
	value: number;
}

export interface AlertStatePayload {
	alertId: string;
	state: "firing" | "acknowledged";
	forMinutes?: number;
}

export interface ConditionNode {
	type: ConditionKind;
	/**
	 * Editor-only stable identity for keyed `{#each}` blocks. Set on construction
	 * (`defaultPayload`/`nodeFromApi`); stripped before the node is sent over the
	 * wire by `stripEditorFields`. Index-based keys cause Svelte 5 to re-bind the
	 * wrong nested editor instance after a sibling is removed; this avoids that.
	 */
	_uid?: string;
	composite?: CompositePayload;
	not?: NotPayload;
	sustained?: SustainedPayload;
	threshold?: ThresholdPayload;
	rate_of_change?: RateOfChangePayload;
	staleness?: StalenessPayload;
	predicted?: PredictedPayload;
	trend?: TrendPayload;
	time_of_day?: TimeOfDayPayload;
	iob?: IobPayload;
	cob?: CobPayload;
	reservoir?: ReservoirPayload;
	site_age?: SiteAgePayload;
	sensor_age?: SensorAgePayload;
	alert_state?: AlertStatePayload;
}

function newUid(): string {
	if (typeof crypto !== "undefined" && "randomUUID" in crypto) {
		return crypto.randomUUID();
	}
	return Math.random().toString(36).slice(2);
}

// ---------------------------------------------------------------------------
// Default payloads per kind
// ---------------------------------------------------------------------------

export function defaultPayload(kind: ConditionKind): ConditionNode {
	const node = makeDefault(kind);
	node._uid = newUid();
	return node;
}

function makeDefault(kind: ConditionKind): ConditionNode {
	switch (kind) {
		case "composite":
			return {
				type: "composite",
				composite: {
					operator: "and",
					conditions: [defaultPayload("threshold")],
				},
			};
		case "not":
			return { type: "not", not: { child: defaultPayload("threshold") } };
		case "sustained":
			return {
				type: "sustained",
				sustained: { minutes: 15, child: defaultPayload("threshold") },
			};
		case "threshold":
			return {
				type: "threshold",
				threshold: { direction: "below", value: 70 },
			};
		case "rate_of_change":
			return {
				type: "rate_of_change",
				rate_of_change: { direction: "falling", rate: 3 },
			};
		case "staleness":
			return {
				type: "staleness",
				staleness: { operator: ">=", value: 15 },
			};
		case "predicted":
			return {
				type: "predicted",
				predicted: { operator: "<=", value: 70, withinMinutes: 30 },
			};
		case "trend":
			return { type: "trend", trend: { bucket: "falling" } };
		case "time_of_day":
			return {
				type: "time_of_day",
				time_of_day: { from: "22:00", to: "06:00" },
			};
		case "iob":
			return { type: "iob", iob: { operator: ">=", value: 1 } };
		case "cob":
			return { type: "cob", cob: { operator: ">=", value: 10 } };
		case "reservoir":
			return {
				type: "reservoir",
				reservoir: { operator: "<=", value: 10 },
			};
		case "site_age":
			return {
				type: "site_age",
				site_age: { operator: ">=", value: 72 },
			};
		case "sensor_age":
			return {
				type: "sensor_age",
				sensor_age: { operator: ">=", value: 10 },
			};
		case "alert_state":
			return {
				type: "alert_state",
				alert_state: { alertId: "", state: "firing" },
			};
	}
}

// ---------------------------------------------------------------------------
// (De)serialise to/from API conditionParams field
// ---------------------------------------------------------------------------

/**
 * Wrap a kind + opaque API payload into a `ConditionNode`. The API stores only
 * the kind-specific payload (e.g. `{direction, value}`); the FE keeps the kind
 * alongside it so a single recursive editor can dispatch on `node.type`.
 */
export function nodeFromApi(
	kind: string | undefined,
	params: unknown,
): ConditionNode | null {
	if (!kind) return null;
	if (params === null || params === undefined) return null;
	const k = kind as ConditionKind;
	const node: ConditionNode = { type: k, _uid: newUid() };
	(node as Record<string, unknown>)[k] = params;
	// Recursively assign uids to nested nodes so keyed each-blocks have stable
	// identity for every level of the tree, not just the root.
	assignUidsRecursive(node);
	return node;
}

function assignUidsRecursive(node: ConditionNode): void {
	if (!node._uid) node._uid = newUid();
	if (node.composite?.conditions) {
		for (const child of node.composite.conditions) assignUidsRecursive(child);
	}
	if (node.not?.child) assignUidsRecursive(node.not.child);
	if (node.sustained?.child) assignUidsRecursive(node.sustained.child);
}

/**
 * Returns a deep copy of `node` with every editor-only `_uid` field stripped.
 * Use this when sending the full ConditionNode envelope to the backend (e.g.
 * `autoResolveParams`) so the editor's internal identity doesn't leak into
 * stored configuration.
 */
export function stripEditorFields(node: ConditionNode): ConditionNode {
	const cleaned: ConditionNode = { type: node.type };
	for (const k of Object.keys(node) as (keyof ConditionNode)[]) {
		if (k === "_uid" || k === "type") continue;
		const value = node[k];
		if (value === undefined) continue;
		// Recurse into nested children so uids in the subtree are also stripped.
		if (k === "composite" && node.composite) {
			cleaned.composite = {
				operator: node.composite.operator,
				conditions: node.composite.conditions.map(stripEditorFields),
			};
		} else if (k === "not" && node.not) {
			cleaned.not = { child: stripEditorFields(node.not.child) };
		} else if (k === "sustained" && node.sustained) {
			cleaned.sustained = {
				minutes: node.sustained.minutes,
				child: stripEditorFields(node.sustained.child),
			};
		} else {
			(cleaned as Record<string, unknown>)[k] = value;
		}
	}
	return cleaned;
}

/**
 * Extract the kind-specific payload for the API. Returns `null` when there's
 * no node.
 */
export function nodeToApi(
	node: ConditionNode | null,
): { conditionType: string; conditionParams: unknown } | null {
	if (!node) return null;
	const params = (node as Record<string, unknown>)[node.type];
	return { conditionType: node.type, conditionParams: params ?? {} };
}

// ---------------------------------------------------------------------------
// Existing client-config / schedule shapes (unchanged)
// ---------------------------------------------------------------------------

export interface AudioConfig {
	enabled: boolean;
	sound: string;
	customSoundId: string | null;
	ascending: boolean;
	startVolume: number;
	maxVolume: number;
	ascendDurationSeconds: number;
	repeatCount: number;
}

export interface VisualConfig {
	flashEnabled: boolean;
	flashColor: string;
	persistentBanner: boolean;
	wakeScreen: boolean;
}

export interface SnoozeConfig {
	defaultMinutes: number;
	options: number[];
	maxCount: number;
	smartSnooze: boolean;
	smartSnoozeExtendMinutes: number;
	/**
	 * Optional conditions that must all hold for the snooze to extend. When
	 * empty, the backend falls back to the trend-favorable heuristic.
	 */
	conditions: ConditionNode[];
}

export interface ClientConfiguration {
	audio: AudioConfig;
	visual: VisualConfig;
	snooze: SnoozeConfig;
}

export interface EditableChannel {
	channelType: ChannelType | string;
	destination: string;
	destinationLabel: string;
}

export interface EditableStep {
	stepOrder: number;
	delaySeconds: number;
	channels: EditableChannel[];
}

export interface EditableSchedule {
	name: string;
	isDefault: boolean;
	daysOfWeek: number[];
	startTime: string;
	endTime: string;
	timezone: string;
	escalationSteps: EditableStep[];
	expanded: boolean;
}

export interface RuleEditorState {
	name: string;
	description: string;
	severity: AlertRuleSeverity;
	condition: ConditionNode | null;
	autoResolveEnabled: boolean;
	autoResolveCondition: ConditionNode | null;
	sortOrder: number;
	isEnabled: boolean;
	clientConfig: ClientConfiguration;
	schedules: EditableSchedule[];
}

export function defaultSchedule(): EditableSchedule {
	return {
		name: "Default Schedule",
		isDefault: true,
		daysOfWeek: [],
		startTime: "00:00",
		endTime: "23:59",
		timezone: "UTC",
		escalationSteps: [
			{
				stepOrder: 0,
				delaySeconds: 0,
				channels: [
					{
						channelType: ChannelType.WebPush,
						destination: "",
						destinationLabel: "",
					},
				],
			},
		],
		expanded: true,
	};
}

export function defaultClientConfig(): ClientConfiguration {
	return {
		audio: {
			enabled: true,
			sound: "alarm-default",
			customSoundId: null,
			ascending: false,
			startVolume: 50,
			maxVolume: 80,
			ascendDurationSeconds: 30,
			repeatCount: 2,
		},
		visual: {
			flashEnabled: false,
			flashColor: "#ff0000",
			persistentBanner: true,
			wakeScreen: false,
		},
		snooze: {
			defaultMinutes: 15,
			options: [5, 15, 30, 60],
			maxCount: 5,
			smartSnooze: false,
			smartSnoozeExtendMinutes: 10,
			conditions: [],
		},
	};
}

function defaultState(): RuleEditorState {
	return {
		name: "",
		description: "",
		severity: AlertRuleSeverity.Warning,
		condition: defaultPayload("threshold"),
		autoResolveEnabled: false,
		autoResolveCondition: null,
		sortOrder: 0,
		isEnabled: true,
		clientConfig: defaultClientConfig(),
		schedules: [defaultSchedule()],
	};
}

/**
 * Snooze conditions are a frontend-only client-config extension. NSwag's
 * generated type for `clientConfiguration.snooze` doesn't model them, so we
 * read them off the raw object defensively.
 */
function parseSnoozeConditions(snooze: unknown): ConditionNode[] {
	if (!snooze || typeof snooze !== "object") return [];
	const raw = (snooze as Record<string, unknown>).conditions;
	if (!Array.isArray(raw)) return [];
	const out: ConditionNode[] = [];
	for (const entry of raw) {
		if (entry && typeof entry === "object" && typeof (entry as { type?: unknown }).type === "string") {
			out.push(entry as ConditionNode);
		}
	}
	return out;
}

export function parseRule(r: AlertRuleResponse | null): RuleEditorState {
	if (!r) return defaultState();

	const condition = nodeFromApi(r.conditionType, r.conditionParams) ?? defaultPayload("threshold");
	const autoResolveCondition = nodeFromApi("composite", r.autoResolveParams);

	// Client configuration
	const cc = r.clientConfiguration;
	const clientConfig: ClientConfiguration = cc
		? {
				audio: {
					enabled: cc.audio?.enabled ?? true,
					sound: cc.audio?.sound ?? "alarm-default",
					customSoundId: cc.audio?.customSoundId ?? null,
					ascending: cc.audio?.ascending ?? false,
					startVolume: cc.audio?.startVolume ?? 50,
					maxVolume: cc.audio?.maxVolume ?? 80,
					ascendDurationSeconds: cc.audio?.ascendDurationSeconds ?? 30,
					repeatCount: cc.audio?.repeatCount ?? 2,
				},
				visual: {
					flashEnabled: cc.visual?.flashEnabled ?? false,
					flashColor: cc.visual?.flashColor ?? "#ff0000",
					persistentBanner: cc.visual?.persistentBanner ?? true,
					wakeScreen: cc.visual?.wakeScreen ?? false,
				},
				snooze: {
					defaultMinutes: cc.snooze?.defaultMinutes ?? 15,
					options: cc.snooze?.options ?? [5, 15, 30, 60],
					maxCount: cc.snooze?.maxCount ?? 5,
					smartSnooze: cc.snooze?.smartSnooze ?? false,
					smartSnoozeExtendMinutes: cc.snooze?.smartSnoozeExtendMinutes ?? 10,
					conditions: parseSnoozeConditions(cc.snooze),
				},
			}
		: defaultClientConfig();

	// Schedules
	const schedules: EditableSchedule[] =
		r.schedules && r.schedules.length > 0
			? r.schedules.map((s) => ({
					name: s.name ?? "Default Schedule",
					isDefault: s.isDefault ?? false,
					daysOfWeek: s.daysOfWeek ?? [],
					startTime: s.startTime ?? "00:00",
					endTime: s.endTime ?? "23:59",
					timezone: s.timezone ?? "UTC",
					escalationSteps: (s.escalationSteps ?? [])
						.sort((a, b) => (a.stepOrder ?? 0) - (b.stepOrder ?? 0))
						.map((step) => ({
							stepOrder: step.stepOrder ?? 0,
							delaySeconds: step.delaySeconds ?? 0,
							channels: (step.channels ?? []).map((ch) => ({
								channelType: ch.channelType ?? ChannelType.WebPush,
								destination: ch.destination ?? "",
								destinationLabel: ch.destinationLabel ?? "",
							})),
						})),
					expanded: false,
				}))
			: [defaultSchedule()];

	return {
		name: r.name ?? "",
		description: r.description ?? "",
		severity: (r.severity as AlertRuleSeverity) ?? AlertRuleSeverity.Warning,
		isEnabled: r.isEnabled ?? true,
		sortOrder: r.sortOrder ?? 0,
		condition,
		autoResolveEnabled: r.autoResolveEnabled ?? false,
		autoResolveCondition,
		clientConfig,
		schedules,
	};
}
