import type { ReplayTick } from "./wire";

/** Every reading is synthetic: shaped to show one behaviour, not taken from anyone's data. */
export interface Reading {
	at: number;
	mgdl: number;
	/** mg/dL per minute, as a CGM reports it with the reading. */
	trendRate: number;
}

export interface CarbEntry {
	at: number;
	grams: number;
	label: string;
	/** Minutes to absorb all of it, linearly from the entry. */
	absorbMinutes: number;
}

export interface BolusEntry {
	at: number;
	units: number;
}

export interface Scenario {
	id: ScenarioId;
	label: string;
	description: string;
	start: number;
	end: number;
	readings: Reading[];
	carbs: CarbEntry[];
	boluses: BolusEntry[];
}

export type ScenarioId =
	| "overnight-low"
	| "brief-dip"
	| "fast-fall"
	| "sensor-gap"
	| "meal-high"
	| "missed-bolus"
	| "treated-low";

const MINUTE = 60_000;
const STEP = 5 * MINUTE;
/** How long a bolus stays on board. */
const INSULIN_ACTION_MINUTES = 240;
/**
 * A Monday, on the reader's own clock: the app's chart draws local time, so the times in the
 * prose read true there, and the engine is told the same zone.
 */
const DAY = new Date(2026, 0, 5).getTime();
const TIME_ZONE = Intl.DateTimeFormat().resolvedOptions().timeZone;

/** `HH:mm` on the fixture day; hours past 23 run into the next day. */
function at(hhmm: string): number {
	const [h, m] = hhmm.split(":").map(Number);
	return DAY + (h * 60 + m) * MINUTE;
}

/**
 * Readings every 5 minutes along straight lines between `points`, with a small fixed wobble so
 * the trace reads as a sensor rather than a ruler. Readings inside a `gaps` span are dropped.
 */
function trace(
	points: [string, number][],
	gaps: [string, string][] = [],
	wobble = 1.5,
): Reading[] {
	const keys = points.map(([t, v]) => [at(t), v] as const);
	const holes = gaps.map(([from, to]) => [at(from), at(to)] as const);
	const values: { at: number; mgdl: number }[] = [];
	for (let t = keys[0][0]; t <= keys[keys.length - 1][0]; t += STEP) {
		const i = keys.findIndex(([k], j) => j < keys.length - 1 && t >= k && t <= keys[j + 1][0]);
		const [t0, v0] = keys[i];
		const [t1, v1] = keys[i + 1];
		const linear = v0 + ((v1 - v0) * (t - t0)) / (t1 - t0);
		const step = (t - keys[0][0]) / STEP;
		values.push({ at: t, mgdl: Math.round(linear + wobble * Math.sin(step * 1.7)) });
	}
	return values
		.map((r, i) => {
			const neighbour = values[i - 1] ?? values[i + 1];
			const delta = i === 0 ? neighbour.mgdl - r.mgdl : r.mgdl - neighbour.mgdl;
			return { ...r, trendRate: Math.round((delta / 5) * 10) / 10 };
		})
		.filter((r) => !holes.some(([from, to]) => r.at > from && r.at < to));
}

const meal = (time: string, grams: number, label: string, absorbMinutes = 180): CarbEntry => ({
	at: at(time),
	grams,
	label,
	absorbMinutes,
});

const bolus = (time: string, units: number): BolusEntry => ({ at: at(time), units });

export const SCENARIOS: Record<ScenarioId, Scenario> = {
	"overnight-low": {
		id: "overnight-low",
		label: "Overnight low",
		description: "A slow drift down after midnight that stays low for most of an hour.",
		start: at("23:00"),
		end: at("27:00"),
		readings: trace([
			["23:00", 124],
			["23:40", 112],
			["24:40", 72],
			["25:10", 59],
			["25:30", 60],
			["25:55", 69],
			["26:15", 84],
			["27:00", 98],
		]),
		carbs: [],
		boluses: [],
	},
	"brief-dip": {
		id: "brief-dip",
		label: "Brief dip",
		description: "Steady in the 80s, with three readings just under 70 in the middle of the afternoon.",
		start: at("13:00"),
		end: at("16:00"),
		readings: trace(
			[
				["13:00", 88],
				["13:50", 82],
				["14:00", 76],
				["14:05", 68],
				["14:15", 67],
				["14:20", 74],
				["14:35", 84],
				["16:00", 86],
			],
			[],
			1,
		),
		carbs: [],
		boluses: [],
	},
	"fast-fall": {
		id: "fast-fall",
		label: "Fast fall",
		description: "Steady at 150, then a fall of about 3 mg/dL a minute during exercise.",
		start: at("16:00"),
		end: at("18:30"),
		readings: trace(
			[
				["16:00", 150],
				["16:45", 152],
				["17:20", 66],
				["17:35", 61],
				["17:55", 74],
				["18:30", 104],
			],
			[],
			0.8,
		),
		carbs: [],
		boluses: [],
	},
	"sensor-gap": {
		id: "sensor-gap",
		label: "Sensor gap",
		description: "Readings stop for 40 minutes, as when the phone and the sensor lose touch.",
		start: at("02:00"),
		end: at("05:00"),
		readings: trace(
			[
				["02:00", 112],
				["03:00", 104],
				["03:40", 98],
				["05:00", 108],
			],
			[["03:00", "03:40"]],
		),
		carbs: [],
		boluses: [],
	},
	"meal-high": {
		id: "meal-high",
		label: "High after dinner",
		description: "Dinner with its insulin, a rise to the high 200s, and a fall that starts while still over 250.",
		start: at("18:00"),
		end: at("23:00"),
		readings: trace(
			[
				["18:00", 112],
				["18:30", 120],
				["19:30", 284],
				["19:55", 282],
				["20:40", 228],
				["21:30", 176],
				["22:00", 158],
				["23:00", 140],
			],
			[],
			0.6,
		),
		carbs: [meal("18:10", 70, "Dinner")],
		boluses: [bolus("18:10", 6)],
	},
	"missed-bolus": {
		id: "missed-bolus",
		label: "Missed bolus",
		description: "Lunch with no insulin, a long high, then a correction at 15:30 that brings it down.",
		start: at("12:00"),
		end: at("17:30"),
		readings: trace(
			[
				["12:00", 108],
				["12:40", 118],
				["13:40", 238],
				["14:20", 262],
				["15:30", 256],
				["16:30", 190],
				["17:30", 142],
			],
			[],
			0.8,
		),
		carbs: [meal("12:30", 50, "Lunch")],
		boluses: [bolus("15:30", 4)],
	},
	"treated-low": {
		id: "treated-low",
		label: "Treated low",
		description: "A fall in the afternoon, caught early with 15 g of fast carbs. Glucose still dips under 70 before they work.",
		start: at("14:30"),
		end: at("17:30"),
		readings: trace(
			[
				["14:30", 104],
				["15:10", 84],
				["15:20", 76],
				["15:30", 69],
				["15:40", 64],
				["15:50", 67],
				["16:00", 74],
				["16:30", 92],
				["17:30", 104],
			],
			[],
			0.8,
		),
		carbs: [meal("15:20", 15, "Juice", 60)],
		boluses: [],
	},
};

/** Grams not yet absorbed at `t`. */
export function carbsOnBoard(scenario: Scenario, t: number): number {
	return scenario.carbs.reduce((sum, c) => {
		const elapsed = (t - c.at) / MINUTE;
		return elapsed < 0 ? sum : sum + c.grams * Math.max(0, 1 - elapsed / c.absorbMinutes);
	}, 0);
}

/** Units still acting at `t`: flat at first, then tailing off to nothing (an illustrative curve). */
export function insulinOnBoard(scenario: Scenario, t: number): number {
	return scenario.boluses.reduce((sum, b) => {
		const x = (t - b.at) / MINUTE / INSULIN_ACTION_MINUTES;
		return x < 0 || x >= 1 ? sum : sum + b.units * (1 - x) ** 2 * (1 + 2 * x);
	}, 0);
}

function latestAtOrBefore(entries: { at: number }[], t: number): number | undefined {
	let latest: number | undefined;
	for (const e of entries) if (e.at <= t && (latest === undefined || e.at > latest)) latest = e.at;
	return latest;
}

function iso(ms: number): string {
	return new Date(ms).toISOString().replace(".000Z", "Z");
}

/** The 5-minute instants a scenario is replayed at, from its start up to its end. */
export function tickTimes(scenario: Scenario): number[] {
	const times: number[] = [];
	for (let t = scenario.start; t < scenario.end; t += STEP) times.push(t);
	return times;
}

/**
 * The context each 5-minute tick is evaluated against, built as the API's replay does
 * (AlertReplayService): the newest reading at or before the tick, however old, and a tick before
 * the first reading reading as fresh rather than as no reading ever.
 */
export function ticksFor(scenario: Scenario): ReplayTick[] {
	let next = 0;
	let current: Reading | undefined;
	return tickTimes(scenario).map((t) => {
		while (next < scenario.readings.length && scenario.readings[next].at <= t) {
			current = scenario.readings[next++];
		}
		const lastCarb = latestAtOrBefore(scenario.carbs, t);
		const lastBolus = latestAtOrBefore(scenario.boluses, t);
		const treatments = {
			iob_units: Math.round(insulinOnBoard(scenario, t) * 100) / 100,
			cob_grams: Math.round(carbsOnBoard(scenario, t) * 10) / 10,
			...(lastCarb === undefined ? {} : { last_carb_at: iso(lastCarb) }),
			...(lastBolus === undefined ? {} : { last_bolus_at: iso(lastBolus) }),
		};
		return {
			at: iso(t),
			context: current
				? {
						latest_value: current.mgdl,
						latest_timestamp: iso(current.at),
						trend_rate: current.trendRate,
						last_reading_at: iso(current.at),
						...treatments,
						tenant_time_zone_id: TIME_ZONE,
					}
				: { last_reading_at: iso(t), ...treatments, tenant_time_zone_id: TIME_ZONE },
		};
	});
}
