import type { ScaleTime } from 'd3-scale';

export const MS_PER_HOUR = 3_600_000;
export const HOURS_PER_DAY = 24;
export const HOURS_PER_ROW = 48;

export interface ActogramPoint {
	mills: number;
	[key: string]: unknown;
}

export interface GlucosePoint extends ActogramPoint {
	sgv: number;
	color: string;
}

export interface GlucoseThresholds {
	low: number;
	high: number;
	veryLow: number;
	veryHigh: number;
	glucoseYMax: number;
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
	day: Date;
}

export interface ActogramTooltipData {
	time: Date;
	bgPoint?: RowDataPoint<GlucosePoint>;
	dataPoint?: RowDataPoint<ActogramPoint>;
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

function slicePoints<T extends ActogramPoint>(data: T[], days: Date[]): { day: Date; data: RowDataPoint<T>[] }[] {
	const rows = days.map((day) => ({
		day,
		data: [] as RowDataPoint<T>[],
	}));

	for (const point of data) {
		for (let i = 0; i < days.length; i++) {
			const dayStart = days[i].getTime();
			const offset = point.mills - dayStart;
			const hoursFromStart = offset / MS_PER_HOUR;

			if (hoursFromStart >= 0 && hoursFromStart < HOURS_PER_DAY) {
				rows[i].data.push({ point, hoursFromStart, isExtended: false });
			} else if (hoursFromStart >= HOURS_PER_DAY && hoursFromStart < HOURS_PER_ROW) {
				rows[i].data.push({ point, hoursFromStart, isExtended: true });
			}
		}
	}

	return rows;
}

export function sliceIntoRows<T extends ActogramPoint>(
	data: T[],
	days: Date[],
): { day: Date; data: RowDataPoint<T>[] }[] {
	return slicePoints(data, days);
}

export function sliceBgIntoRows(
	data: GlucosePoint[],
	days: Date[],
): { day: Date; bgData: RowDataPoint<GlucosePoint>[] }[] {
	return slicePoints(data, days).map((row) => ({
		day: row.day,
		bgData: row.data,
	}));
}
