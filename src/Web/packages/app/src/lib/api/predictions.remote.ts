/**
 * Remote functions for glucose predictions using oref algorithms
 */
import { getRequestEvent, query } from '$app/server';
import { z } from 'zod';
import { error } from '@sveltejs/kit';

const getPredictionsSchema = z.object({
	profileId: z.string().optional(),
});

/**
 * Prediction point with timestamp for charting
 */
export interface PredictionPoint {
	timestamp: number;
	value: number;
}

/**
 * Prediction curves with timestamps for visualization
 */
export interface PredictionCurves {
	/** Main prediction curve */
	main: PredictionPoint[];
	/** IOB-only prediction */
	iobOnly: PredictionPoint[];
	/** UAM prediction */
	uam: PredictionPoint[];
	/** COB prediction */
	cob: PredictionPoint[];
	/** Zero-temp prediction */
	zeroTemp: PredictionPoint[];
}

/**
 * Transformed prediction response for the frontend
 */
export interface PredictionData {
	timestamp: Date;
	currentBg: number;
	delta: number;
	eventualBg: number;
	iob: number;
	cob: number;
	sensitivityRatio: number | null;
	intervalMinutes: number;
	curves: PredictionCurves;
}

/**
 * Get glucose predictions based on current data.
 * Returns predicted glucose values with timestamps for charting.
 */
export const getPredictions = query(getPredictionsSchema, async (props) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		const response = await apiClient.predictions.getPredictions(props.profileId);

		// Get the base timestamp from the response
		const baseTimestamp = response.timestamp
			? new Date(response.timestamp).getTime()
			: Date.now();
		const intervalMs = (response.intervalMinutes || 5) * 60 * 1000;

		// Helper to convert prediction array to timestamped points
		const toPoints = (predictions: number[] | undefined | null): PredictionPoint[] => {
			if (!predictions) return [];
			return predictions.map((value, index) => ({
				timestamp: baseTimestamp + index * intervalMs,
				value,
			}));
		};

		return {
			timestamp: response.timestamp ? new Date(response.timestamp) : new Date(),
			currentBg: response.currentBg || 0,
			delta: response.delta || 0,
			eventualBg: response.eventualBg || 0,
			iob: response.iob || 0,
			cob: response.cob || 0,
			sensitivityRatio: response.sensitivityRatio ?? null,
			intervalMinutes: response.intervalMinutes || 5,
			curves: {
				main: toPoints(response.predictions?.default),
				iobOnly: toPoints(response.predictions?.iobOnly),
				uam: toPoints(response.predictions?.uam),
				cob: toPoints(response.predictions?.cob),
				zeroTemp: toPoints(response.predictions?.zeroTemp),
			},
		} satisfies PredictionData;
	} catch (err) {
		console.error('Error loading predictions:', err);
		throw error(500, 'Failed to load predictions');
	}
});

/**
 * Get the status of the prediction service
 */
export const getPredictionStatus = query(z.object({}), async () => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		const response = await apiClient.predictions.getStatus();
		return {
			available: response.available ?? false,
			source: response.source ?? null,
		};
	} catch (err) {
		console.error('Error checking prediction status:', err);
		return {
			available: false,
			source: null,
		};
	}
});
