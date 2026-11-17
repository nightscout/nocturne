import { SyncDataType } from '#lib/api/generated/nocturne-api-client.js';

/**
 * Human-readable labels for record type keys returned by the API.
 * Uses the SyncDataType enum for type-safe keys, with fallbacks for legacy table names.
 */
const DATA_TYPE_LABELS: Record<SyncDataType, string> = {
	[SyncDataType.Glucose]: 'Glucose',
	[SyncDataType.ManualBG]: 'Manual BG',
	[SyncDataType.Calibrations]: 'Calibrations',
	[SyncDataType.Boluses]: 'Boluses',
	[SyncDataType.BasalInjections]: 'Basal injections',
	[SyncDataType.CarbIntake]: 'Carb intake',
	[SyncDataType.TempBasals]: 'Basal',
	[SyncDataType.BGChecks]: 'BG checks',
	[SyncDataType.BolusCalculations]: 'Bolus calculations',
	[SyncDataType.Notes]: 'Notes',
	[SyncDataType.DeviceEvents]: 'Device events',
	[SyncDataType.StateSpans]: 'State spans',
	[SyncDataType.Profiles]: 'Profiles',
	[SyncDataType.DeviceStatus]: 'Device status',
	[SyncDataType.Activity]: 'Activity',
	[SyncDataType.Food]: 'Food',
	[SyncDataType.Steps]: 'Steps',
	[SyncDataType.HeartRate]: 'Heart rate',
	[SyncDataType.BodyWeight]: 'Body weight',
	[SyncDataType.Sleep]: 'Sleep',
};

const LABELS_BY_KEY: ReadonlyMap<string, string> = new Map([
	...Object.entries(DATA_TYPE_LABELS),
	// Legacy table keys (used when legacy tables still contain data)
	['Entries', 'Entries (Legacy)'],
	['Treatments', 'Treatments (Legacy)'],
]);

/** Get a human-readable label for a data type key */
export function getDataTypeLabel(key: string): string {
	return LABELS_BY_KEY.get(key) || key;
}
