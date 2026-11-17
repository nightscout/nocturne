import { describe, it, expect } from 'vitest';
import {
	sliceIntoRows,
	sliceBgIntoRows,
	findNearestPoint,
	hasShiftedHourMarks,
	MS_PER_HOUR,
	resolveTargetRange,
	type ActogramPoint,
	type GlucosePoint,
	type RowDataPoint,
} from './actogram';
import { servedDay, servedDays } from '#lib/test-stubs/actogram-days.js';

describe('sliceIntoRows', () => {
	const days = servedDays('2026-04-20', 3);

	it('assigns a point to the correct row primary window', () => {
		const data: ActogramPoint[] = [
			{ mills: new Date('2026-04-21T10:00:00Z').getTime() },
		];
		const rows = sliceIntoRows(data, days);
		const apr21Row = rows.find((r) => r.day === days[1]);
		expect(apr21Row?.data).toHaveLength(1);
		expect(apr21Row?.data[0].hoursFromStart).toBeCloseTo(10);
	});

	it('assigns a point to the previous row extended window', () => {
		const data: ActogramPoint[] = [
			{ mills: new Date('2026-04-21T10:00:00Z').getTime() },
		];
		const rows = sliceIntoRows(data, days);
		const apr20Row = rows.find((r) => r.day === days[0]);
		expect(apr20Row?.data).toHaveLength(1);
		expect(apr20Row?.data[0].hoursFromStart).toBeCloseTo(34);
		expect(apr20Row?.data[0].isExtended).toBe(true);
	});

	it('returns one row per day in input order', () => {
		const rows = sliceIntoRows([], days);
		expect(rows).toHaveLength(3);
		expect(rows[0].day).toEqual(days[0]);
		expect(rows[2].day).toEqual(days[2]);
	});

	it('handles empty data', () => {
		const rows = sliceIntoRows([], days);
		expect(rows.every((r) => r.data.length === 0)).toBe(true);
	});

	it('point at midnight belongs to primary window of that day', () => {
		const data: ActogramPoint[] = [
			{ mills: new Date('2026-04-21T00:00:00Z').getTime() },
		];
		const rows = sliceIntoRows(data, days);
		const apr21Row = rows.find((r) => r.day === days[1]);
		expect(apr21Row?.data.some((d) => d.hoursFromStart === 0 && !d.isExtended)).toBe(true);
	});
});

describe('sliceIntoRows on served days', () => {
	const chatham = 13 * 60 + 45;

	it('cuts a row where the served day ends, not at the browser calendar day', () => {
		const days = servedDays('2026-01-10', 2, chatham);
		const lastOfTenth = days[0].endMills - 1;
		const rows = sliceIntoRows([{ mills: days[0].startMills }, { mills: lastOfTenth }, { mills: days[0].endMills }], days);

		expect(rows[0].data.map((d) => d.isExtended)).toEqual([false, false, true]);
		expect(rows[1].data.map((d) => d.isExtended)).toEqual([false]);
		expect(rows[1].data[0].hoursFromStart).toBe(0);
	});

	it('lays a 23 hour day out as 23 primary hours', () => {
		const short = servedDay('2023-03-12', 0, 23);
		const rows = sliceIntoRows([{ mills: short.startMills + 22.5 * MS_PER_HOUR }, { mills: short.endMills }], [short]);

		expect(rows[0].data.map((d) => d.isExtended)).toEqual([false, true]);
		expect(rows[0].data[1].hoursFromStart).toBe(23);
	});

	it('lays a 25 hour day out as 25 primary hours', () => {
		const long = servedDay('2023-11-05', 0, 25);
		const rows = sliceIntoRows([{ mills: long.startMills + 24.5 * MS_PER_HOUR }, { mills: long.endMills }], [long]);

		expect(rows[0].data.map((d) => d.isExtended)).toEqual([false, true]);
	});
});

describe('findNearestPoint', () => {
	const points: RowDataPoint<ActogramPoint>[] = [
		{ point: { mills: 100 }, hoursFromStart: 2, isExtended: false },
		{ point: { mills: 200 }, hoursFromStart: 5, isExtended: false },
		{ point: { mills: 300 }, hoursFromStart: 10, isExtended: false },
	];

	it('returns the nearest point by hoursFromStart', () => {
		const result = findNearestPoint(points, 4.5);
		expect(result?.point.mills).toBe(200);
	});

	it('returns undefined for empty array', () => {
		expect(findNearestPoint([], 5)).toBeUndefined();
	});

	it('returns undefined when nearest point is beyond maxDistanceHours', () => {
		expect(findNearestPoint(points, 30, 1)).toBeUndefined();
	});

	it('returns nearest even in extended window', () => {
		const extended: RowDataPoint<ActogramPoint>[] = [
			{ point: { mills: 400 }, hoursFromStart: 26, isExtended: true },
		];
		const result = findNearestPoint(extended, 25.5);
		expect(result?.point.mills).toBe(400);
	});
});

describe('resolveTargetRange', () => {
	const clinical = {
		low: 70,
		high: 180,
		veryLow: 54,
		veryHigh: 250,
		glucoseYMax: 300,
		targetLow: null,
		targetHigh: null,
	};

	it('prefers the personal target when the profile supplies one', () => {
		expect(
			resolveTargetRange({ ...clinical, targetLow: 95, targetHigh: 115 }),
		).toEqual({
			low: 95,
			high: 115,
		});
	});

	it('falls back to the clinical band when there is no target', () => {
		expect(resolveTargetRange(clinical)).toEqual({ low: 70, high: 180 });
	});
});

describe('sliceBgIntoRows', () => {
	const days = servedDays('2026-04-20', 3);

	it('places a glucose point in the primary window with correct shape', () => {
		const bg: GlucosePoint[] = [
			{ mills: new Date('2026-04-21T06:30:00Z').getTime(), sgv: 120, color: 'in-range' },
		];
		const rows = sliceBgIntoRows(bg, days);
		const apr21Row = rows.find((r) => r.day === days[1]);
		expect(apr21Row?.bgData).toHaveLength(1);
		expect(apr21Row?.bgData[0].hoursFromStart).toBeCloseTo(6.5);
		expect(apr21Row?.bgData[0].isExtended).toBe(false);
		expect(apr21Row?.bgData[0].point.sgv).toBe(120);
		expect(apr21Row?.bgData[0].point.color).toBe('in-range');
	});

	it('double-plots a glucose point in both primary and previous-row extended windows', () => {
		const bg: GlucosePoint[] = [
			{ mills: new Date('2026-04-21T14:00:00Z').getTime(), sgv: 95, color: 'in-range' },
		];
		const rows = sliceBgIntoRows(bg, days);

		const apr21Row = rows.find((r) => r.day === days[1]);
		expect(apr21Row?.bgData).toHaveLength(1);
		expect(apr21Row?.bgData[0].hoursFromStart).toBeCloseTo(14);
		expect(apr21Row?.bgData[0].isExtended).toBe(false);

		const apr20Row = rows.find((r) => r.day === days[0]);
		expect(apr20Row?.bgData).toHaveLength(1);
		expect(apr20Row?.bgData[0].hoursFromStart).toBeCloseTo(38);
		expect(apr20Row?.bgData[0].isExtended).toBe(true);
	});
});

describe('hasShiftedHourMarks', () => {
	it.each([
		{ hours: 24, nextHours: 24, shifted: false },
		{ hours: 23, nextHours: 24, shifted: true },
		{ hours: 25, nextHours: 24, shifted: true },
		{ hours: 24, nextHours: 23, shifted: true },
		{ hours: 24, nextHours: 25, shifted: true },
	])('is $shifted for a $hours hour day before a $nextHours hour day', ({ hours, nextHours, shifted }) => {
		expect(hasShiftedHourMarks(servedDay('2026-03-08', 0, hours, nextHours))).toBe(shifted);
	});
});
