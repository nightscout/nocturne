/**
 * Remote function for actogram report data.
 * Thin wrapper around the lean `getActogram` API endpoint that adds
 * frontend-only glucose color resolution.
 */
import { getRequestEvent, query } from '$app/server';
import { z } from 'zod';
import { error } from '@sveltejs/kit';
import { getGlucoseColor } from '$lib/utils/chart-colors';

const actogramSchema = z.object({
	from: z.number(),
	to: z.number(),
});

export const getActogramData = query(actogramSchema, async ({ from, to }) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		const data = await apiClient.actogram.getActogram(from, to);

		const thresholds = {
			low: data.thresholds?.low ?? 70,
			high: data.thresholds?.high ?? 180,
			veryLow: data.thresholds?.veryLow ?? 54,
			veryHigh: data.thresholds?.veryHigh ?? 250,
			glucoseYMax: data.thresholds?.glucoseYMax ?? 300,
		};

		const glucoseData = (data.glucose ?? []).map((p) => {
			const sgv = p.sgv ?? 0;
			return {
				mills: p.time ?? 0,
				sgv,
				color: getGlucoseColor(sgv, thresholds),
			};
		});

		const stepCounts = (data.stepCounts ?? []).map((s) => ({
			mills: s.time ?? 0,
			metric: s.steps ?? 0,
		}));

		const heartRates = (data.heartRates ?? []).map((h) => ({
			mills: h.time ?? 0,
			bpm: h.bpm ?? 0,
		}));

		const sleepSpans = (data.sleepSpans ?? []).map((s) => ({
			startMills: s.startMills ?? 0,
			endMills: s.endMills ?? s.startMills ?? 0,
			state: s.state ?? 'Unknown',
		}));

		return {
			stepCounts,
			heartRates,
			glucoseData,
			sleepSpans,
			thresholds,
		};
	} catch (err) {
		console.error('Error loading actogram data:', err);
		throw error(500, 'Failed to load actogram data');
	}
});
