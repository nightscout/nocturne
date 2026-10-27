/**
 * Transforms an ApsSnapshot (from V4 device status) into PredictionData
 * compatible with the glucose chart's prediction rendering.
 */
import type { ApsSnapshot } from '#lib/api/index.js';
import type { PredictionData, PredictionPoint } from '#lib/api/predictions.remote.js';

const INTERVAL_MS = 5 * 60 * 1000; // 5-minute intervals (standard APS)

/**
 * Parse a JSON string containing an array of predicted glucose values
 * and convert to timestamped PredictionPoints.
 */
function parsePredictionCurve(
	json: string | undefined | null,
	startMills: number,
): PredictionPoint[] {
	if (!json) return [];
	try {
		const values: number[] = JSON.parse(json);
		if (!Array.isArray(values)) return [];
		return values.map((value, index) => ({
			timestamp: startMills + index * INTERVAL_MS,
			value,
		}));
	} catch {
		return [];
	}
}

/**
 * Convert an ApsSnapshot to PredictionData for the glucose chart.
 * Returns null if the snapshot has no usable prediction data.
 */
export function apsSnapshotToPrediction(snapshot: ApsSnapshot): PredictionData | null {
	const startMills = snapshot.predictedStartMills ?? snapshot.mills;
	if (!startMills) return null;

	const main = parsePredictionCurve(snapshot.predictedDefaultJson, startMills);
	const iobOnly = parsePredictionCurve(snapshot.predictedIobJson, startMills);
	const zeroTemp = parsePredictionCurve(snapshot.predictedZtJson, startMills);
	const cob = parsePredictionCurve(snapshot.predictedCobJson, startMills);
	const uam = parsePredictionCurve(snapshot.predictedUamJson, startMills);

	// If no curves have data, this snapshot has no predictions
	if (main.length === 0 && iobOnly.length === 0 && zeroTemp.length === 0 && cob.length === 0 && uam.length === 0) {
		return null;
	}

	// If there's no "main/default" curve but there are others, use IOB as the main curve
	// (OpenAPS always has IOB; Loop uses "default")
	const effectiveMain = main.length > 0 ? main : iobOnly;

	return {
		timestamp: new Date(snapshot.mills ?? startMills),
		currentBg: snapshot.currentBg ?? 0,
		delta: 0, // Not stored in APS snapshots
		eventualBg: snapshot.eventualBg ?? 0,
		iob: snapshot.iob ?? 0,
		cob: snapshot.cob ?? 0,
		sensitivityRatio: snapshot.sensitivityRatio ?? null,
		intervalMinutes: 5,
		curves: {
			main: effectiveMain,
			iobOnly,
			uam,
			cob,
			zeroTemp,
		},
	};
}
