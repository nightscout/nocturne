import type { ScaleTime } from 'd3-scale';
import type { ActogramDay as ServedActogramDay } from '#lib/api/index.js';
import type { ChartThresholds } from '#lib/constants/glucose-thresholds.js';
import { formatShortDate } from '#lib/utils/formatting.js';
import { localDayStart } from '#lib/utils/timezone.js';

export const MS_PER_HOUR = 3_600_000;
export const MS_PER_DAY = 86_400_000;
export const HOURS_PER_DAY = 24;
export const HOURS_PER_ROW = 48;

/**
 * Days either side of the selected range that actogram pages fetch, so each row's
 * next-day double plot has data and scrolling back has context.
 */
export const ACTOGRAM_PADDING_DAYS = 14;

/** A day the API served: the row runs from `startMills` to `endMills`, so a DST day is 23 or 25 hours. */
export type ActogramDay = Required<ServedActogramDay>;

export const HOUR_MARK_SPACING = 6;
const HOUR_MARKS_PER_DAY = 3;

export interface HourMark {
	hour: number;
	mills: number;
}

/** The local hour boundaries the row labels: 0h at the day's start and end, and the served marks between. */
export function hourMarksOf(day: ActogramDay): HourMark[] {
	if (day.hourMarks.length !== 2 * HOUR_MARKS_PER_DAY) return [];
	const instants = [
		day.startMills,
		...day.hourMarks.slice(0, HOUR_MARKS_PER_DAY),
		day.endMills,
		...day.hourMarks.slice(HOUR_MARKS_PER_DAY),
	];
	return instants.map((mills, index) => ({ hour: (index * HOUR_MARK_SPACING) % 24, mills }));
}

/** Whether a DST change inside the row moved an hour mark off an even spacing from the row start. */
export function hasShiftedHourMarks(day: ActogramDay): boolean {
	return hourMarksOf(day).some(
		(mark, index) => mark.mills !== day.startMills + index * HOUR_MARK_SPACING * MS_PER_HOUR,
	);
}

export function formatActogramDay(day: ActogramDay): string {
	return formatShortDate(localDayStart(day.date));
}

export interface ActogramPoint {
	mills: number;
	[key: string]: unknown;
}

export interface GlucosePoint extends ActogramPoint {
	sgv: number;
	color: string;
}

/**
 * The band the target key and limit lines draw: the personal target when the
 * profile supplies one, else the clinical in-range band. Dot colouring stays on
 * `low`/`high` regardless.
 */
export function resolveTargetRange(thresholds: ChartThresholds): {
	low: number;
	high: number;
} {
	return {
		low: thresholds.targetLow ?? thresholds.low,
		high: thresholds.targetHigh ?? thresholds.high,
	};
}

export interface RowDataPoint<T extends ActogramPoint = ActogramPoint> {
	point: T;
	hoursFromStart: number;
	isExtended: boolean;
}

export interface ActogramRowContext<T extends ActogramPoint = ActogramPoint> {
	xScale: ScaleTime<number, number>;
	width: number;
	height: number;
	data: RowDataPoint<T>[];
	day: ActogramDay;
}

export interface ActogramTooltipData<T extends ActogramPoint = ActogramPoint> {
	time: Date;
	bgPoint?: RowDataPoint<GlucosePoint>;
	dataPoint?: RowDataPoint<T>;
}

export function findNearestPoint<T extends ActogramPoint>(
	points: RowDataPoint<T>[],
	hoursFromStart: number,
	maxDistanceHours = 2,
): RowDataPoint<T> | undefined {
	if (points.length === 0) return undefined;

	let nearest: RowDataPoint<T> | undefined;
	let minDist = Infinity;

	for (const p of points) {
		const dist = Math.abs(p.hoursFromStart - hoursFromStart);
		if (dist < minDist) {
			minDist = dist;
			nearest = p;
		}
	}

	return minDist <= maxDistanceHours ? nearest : undefined;
}

/** Points whose `mills` fall inside the inclusive window. */
export function pointsInRange<T extends ActogramPoint>(points: T[], from: number, to: number): T[] {
	return points.filter((point) => point.mills >= from && point.mills <= to);
}

/**
 * Lowest and highest `read` value across `items`, or null when there are none.
 * Reduces rather than spreading into `Math.min`/`Math.max`, which is passed one
 * argument per reading and throws RangeError on a long window of per-minute data.
 */
export function extentOf<T>(
	items: readonly T[],
	read: (item: T) => number,
): { min: number; max: number } | null {
	if (items.length === 0) return null;
	let min = Infinity;
	let max = -Infinity;
	for (const item of items) {
		const value = read(item);
		if (value < min) min = value;
		if (value > max) max = value;
	}
	return { min, max };
}

function slicePoints<T extends ActogramPoint>(data: T[], days: ActogramDay[]): { day: ActogramDay; data: RowDataPoint<T>[] }[] {
	const rows = days.map((day): { day: ActogramDay; data: RowDataPoint<T>[] } => ({
		day,
		data: [],
	}));

	for (const point of data) {
		for (let i = 0; i < days.length; i++) {
			const { startMills, endMills } = days[i];
			const hoursFromStart = (point.mills - startMills) / MS_PER_HOUR;
			const dayHours = (endMills - startMills) / MS_PER_HOUR;

			if (hoursFromStart >= 0 && hoursFromStart < dayHours) {
				rows[i].data.push({ point, hoursFromStart, isExtended: false });
			} else if (hoursFromStart >= dayHours && hoursFromStart < HOURS_PER_ROW) {
				rows[i].data.push({ point, hoursFromStart, isExtended: true });
			}
		}
	}

	return rows;
}

export function sliceIntoRows<T extends ActogramPoint>(
	data: T[],
	days: ActogramDay[],
): { day: ActogramDay; data: RowDataPoint<T>[] }[] {
	return slicePoints(data, days);
}

export function sliceBgIntoRows(
	data: GlucosePoint[],
	days: ActogramDay[],
): { day: ActogramDay; bgData: RowDataPoint<GlucosePoint>[] }[] {
	return slicePoints(data, days).map((row) => ({
		day: row.day,
		bgData: row.data,
	}));
}
