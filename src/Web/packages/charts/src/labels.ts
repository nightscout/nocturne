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
