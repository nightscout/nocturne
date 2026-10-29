import { formatGlucoseValue, getUnitLabel, type GlucoseUnits } from '@nocturne/ui/glucose';

export type { GlucoseUnits };

/**
 * How the chart writes times. `locale` is the regional format, which can differ from the
 * interface language; `hour12` is the viewer's 12- or 24-hour preference.
 */
export interface ChartFormat {
	locale: string;
	hour12: boolean;
	/** IANA zone times are written in; the browser's when absent. */
	timeZone?: string;
}

/** A glucose value in the given units, as a number rounded for display. */
export function glucoseValue(mgdl: number, units: GlucoseUnits): number {
	return formatGlucoseValue(mgdl, units);
}

/** "mg/dL" or "mmol/L". */
export function glucoseUnitLabel(units: GlucoseUnits): string {
	return getUnitLabel(units);
}

/**
 * A time of day, e.g. "2:30 pm" or "14:30". `compact` drops the leading zero of the minutes
 * in 12-hour form; `seconds` appends them. A missing or invalid date is "—".
 */
export function formatClockTime(
	date: Date | string | number | null | undefined,
	format: ChartFormat,
	opts: { compact?: boolean; seconds?: boolean } = {}
): string {
	if (date == null) return '—';
	const d = date instanceof Date ? date : new Date(date);
	if (Number.isNaN(d.getTime())) return '—';
	const options: Intl.DateTimeFormatOptions = {
		hour: 'numeric',
		minute: opts.compact && format.hour12 ? 'numeric' : '2-digit',
		hour12: format.hour12,
		timeZone: format.timeZone,
	};
	if (opts.seconds) options.second = '2-digit';
	return d.toLocaleTimeString(format.locale, options);
}

/**
 * An hour-granularity label for a time axis, e.g. "2 pm", or "14" (24-hour output is
 * zero-padded and may carry a locale suffix, e.g. "09 Uhr"). layerchart's own
 * `format="hour"` emits no hour12 token and so follows the browser locale instead.
 */
export function formatHourLabel(date: Date | number, format: ChartFormat): string {
	const d = typeof date === 'number' ? new Date(date) : date;
	return d.toLocaleTimeString(format.locale, {
		hour: 'numeric',
		hour12: format.hour12,
	});
}

/** A date with its weekday and no time, e.g. "Tue, 28 Jul", ordered by the regional format. */
export function formatWeekdayDate(date: Date | string | number, format: ChartFormat): string {
	const d = date instanceof Date ? date : new Date(date);
	return d.toLocaleDateString(format.locale, {
		weekday: 'short',
		month: 'short',
		day: 'numeric',
	});
}

export function formatInsulinDisplay(insulin: number | null | undefined): string {
	if (insulin === undefined || insulin === null) {
		return 'N/A';
	}
	return insulin.toFixed(2);
}

export function formatCarbDisplay(carbs: number | undefined): string {
	if (carbs === undefined || carbs === null) {
		return 'N/A';
	}
	return carbs.toFixed(0);
}
