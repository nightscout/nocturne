import { GlucoseBucket, PumpModeState, StateSpanCategory, TempBasalMetric } from "#lib/api/generated/nocturne-api-client.js";
import { isTrendBucket } from "./types";

const RATE_DIRECTIONS = ["falling", "rising"] as const;
export type RateDirection = (typeof RATE_DIRECTIONS)[number];

export const ALERT_STATES = ["firing", "unacknowledged", "acknowledged"] as const;
export type AlertState = (typeof ALERT_STATES)[number];

function unknownValue(value: never): string {
	return String(value);
}

/** Built per call so wuchale reads them in the current locale. */
export function glucoseBucketLabel(bucket: GlucoseBucket): string {
	switch (bucket) {
		case GlucoseBucket.VeryLow:
			return "Very low";
		case GlucoseBucket.Low:
			return "Low";
		case GlucoseBucket.TightRange:
			return "Tight range";
		case GlucoseBucket.InRange:
			return "In range";
		case GlucoseBucket.High:
			return "High";
		case GlucoseBucket.VeryHigh:
			return "Very high";
		default:
			return unknownValue(bucket);
	}
}

export function pumpModeLabel(mode: PumpModeState): string {
	switch (mode) {
		case PumpModeState.Automatic:
			return "Automatic";
		case PumpModeState.Limited:
			return "Limited";
		case PumpModeState.Manual:
			return "Manual";
		case PumpModeState.Boost:
			return "Boost";
		case PumpModeState.EaseOff:
			return "Ease off";
		case PumpModeState.Sleep:
			return "Sleep";
		case PumpModeState.Exercise:
			return "Exercise";
		case PumpModeState.Liberty:
			return "Liberty";
		case PumpModeState.Suspended:
			return "Suspended";
		case PumpModeState.Off:
			return "Off";
		default:
			return unknownValue(mode);
	}
}

export function stateSpanCategoryLabel(category: StateSpanCategory): string {
	switch (category) {
		case StateSpanCategory.PumpMode:
			return "Pump mode";
		case StateSpanCategory.PumpConnectivity:
			return "Pump connectivity";
		case StateSpanCategory.Override:
			return "Override";
		case StateSpanCategory.Profile:
			return "Profile";
		case StateSpanCategory.Exercise:
			return "Exercise";
		case StateSpanCategory.Illness:
			return "Illness";
		case StateSpanCategory.Travel:
			return "Travel";
		case StateSpanCategory.DataExclusion:
			return "Data exclusion";
		case StateSpanCategory.TemporaryTarget:
			return "Temporary target";
		case StateSpanCategory.Motion:
			return "Motion";
		default:
			return unknownValue(category);
	}
}

function isAlertState(value: string): value is AlertState {
	return ALERT_STATES.some((state) => state === value);
}

function isRateDirection(value: string): value is RateDirection {
	return RATE_DIRECTIONS.some((direction) => direction === value);
}

export function alertStateLabel(state: string): string {
	if (!isAlertState(state)) return state;
	switch (state) {
		case "firing":
			return "Firing";
		case "unacknowledged":
			return "Unacknowledged";
		case "acknowledged":
			return "Acknowledged";
		default:
			return unknownValue(state);
	}
}

export function trendBucketLabel(bucket: string): string {
	if (!isTrendBucket(bucket)) return bucket;
	switch (bucket) {
		case "falling_fast":
			return "Falling fast";
		case "falling":
			return "Falling";
		case "flat":
			return "Flat";
		case "rising":
			return "Rising";
		case "rising_fast":
			return "Rising fast";
		default:
			return unknownValue(bucket);
	}
}

export function rateDirectionLabel(direction: string): string {
	if (!isRateDirection(direction)) return direction;
	switch (direction) {
		case "falling":
			return "Falling";
		case "rising":
			return "Rising";
		default:
			return unknownValue(direction);
	}
}

export function tempBasalMetricLabel(metric: TempBasalMetric): string {
	switch (metric) {
		case TempBasalMetric.Rate:
			return "Rate (U/h)";
		case TempBasalMetric.PercentOfScheduled:
			return "Percent of scheduled";
		default:
			return unknownValue(metric);
	}
}
