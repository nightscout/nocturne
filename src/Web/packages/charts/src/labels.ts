import type { MealPeriod } from './enums.js';

/**
 * Every word the chart draws. The package translates nothing: the host builds this in the
 * viewer's language and passes it in. A function takes values already formatted for display.
 */
export interface ChartLabels {
	glucose: string;
	bolus: string;
	carbs: string;
	iob: string;
	cob: string;
	/** The IOB and COB track's name. */
	iobCob: string;
	basal: string;
	autoBasal: string;
	tempBasal: string;
	scheduled: string;
	scheduledBasal: string;
	suspended: string;
	percent: string;
	pumpMode: string;
	/** The pump mode lane's name. */
	mode: string;
	/** The pump mode shown when no mode span is in view. */
	automatic: string;
	override: string;
	overrides: string;
	profile: string;
	activity: string;
	sleep: string;
	lastPumpSync: string;
	notReportedByPump: string;
	fingerstickBg: string;
	inRange: string;
	veryHigh: string;
	high: string;
	low: string;
	veryLow: string;
	sensor: string;
	site: string;
	reservoir: string;
	battery: string;
	loadingPredictions: string;
	predictionsUnavailable: string;
	/** The basal track's axis unit. */
	unitsPerHour: string;
	/** The IOB track's axis unit. */
	insulinUnits: string;
	/** An insulin amount, e.g. "1.5U". */
	insulinAmount: (units: string) => string;
	/** A carb amount, e.g. "30g". */
	carbAmount: (grams: string) => string;
	/** A basal rate, e.g. "0.80U/h". */
	basalRate: (rate: string) => string;
	/** A long-acting injection: its amount and, when known, the insulin's name. */
	basalInjection: (units: string, insulinName: string | undefined) => string;
	rangeLimits: (low: string, high: string) => string;
	target: (range: string) => string;
	alarms: (count: number) => string;
	scheduledTrackers: (count: number) => string;
	mealPeriod: (period: MealPeriod) => string;
}

const FIRST = "\u0001";
const SECOND = "\u0002";

/**
 * A ChartLabels as plain data, so it can cross to a worker thread: its words as they are, and each
 * function called with stand-ins for its values. A count is kept for one and for many only, which
 * is all a language with two plural forms needs; the static charts draw no counted words.
 */
export interface SerializedChartLabels {
	words: Omit<ChartLabels, ChartLabelFunction>;
	templates: Record<Exclude<ChartLabelFunction, CountedLabel | "mealPeriod">, string>;
	counted: Record<CountedLabel, { one: string; many: string }>;
	mealPeriods: Record<MealPeriod, string>;
}

type ChartLabelFunction =
	| "insulinAmount"
	| "carbAmount"
	| "basalRate"
	| "basalInjection"
	| "rangeLimits"
	| "target"
	| "alarms"
	| "scheduledTrackers"
	| "mealPeriod";

type CountedLabel = "alarms" | "scheduledTrackers";

const MANY = 7;

export function serializeChartLabels(labels: ChartLabels): SerializedChartLabels {
	const { insulinAmount, carbAmount, basalRate, basalInjection, rangeLimits, target, alarms, scheduledTrackers, mealPeriod, ...words } =
		labels;
	return {
		words,
		templates: {
			insulinAmount: insulinAmount(FIRST),
			carbAmount: carbAmount(FIRST),
			basalRate: basalRate(FIRST),
			basalInjection: basalInjection(FIRST, SECOND),
			rangeLimits: rangeLimits(FIRST, SECOND),
			target: target(FIRST),
		},
		counted: {
			alarms: { one: alarms(1), many: alarms(MANY) },
			scheduledTrackers: { one: scheduledTrackers(1), many: scheduledTrackers(MANY) },
		},
		mealPeriods: {
			Breakfast: mealPeriod("Breakfast"),
			Lunch: mealPeriod("Lunch"),
			Dinner: mealPeriod("Dinner"),
			Snack: mealPeriod("Snack"),
			LateNight: mealPeriod("LateNight"),
		},
	};
}

export function deserializeChartLabels(data: SerializedChartLabels): ChartLabels {
	const fill = (template: string, first: string, second = "") => template.replaceAll(FIRST, first).replaceAll(SECOND, second);
	const count = ({ one, many }: { one: string; many: string }) => (n: number) =>
		n === 1 ? one : many.replaceAll(String(MANY), String(n));
	return {
		...data.words,
		insulinAmount: (units) => fill(data.templates.insulinAmount, units),
		carbAmount: (grams) => fill(data.templates.carbAmount, grams),
		basalRate: (rate) => fill(data.templates.basalRate, rate),
		basalInjection: (units, name) => fill(data.templates.basalInjection, units, name ?? ""),
		rangeLimits: (low, high) => fill(data.templates.rangeLimits, low, high),
		target: (range) => fill(data.templates.target, range),
		alarms: count(data.counted.alarms),
		scheduledTrackers: count(data.counted.scheduledTrackers),
		mealPeriod: (period) => data.mealPeriods[period],
	};
}
