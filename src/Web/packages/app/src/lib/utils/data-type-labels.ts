import { SyncDataType } from '$api/generated/nocturne-api-client';

/**
 * Human-readable labels for record type keys returned by the API.
 * Uses the SyncDataType enum for type-safe keys, with fallbacks for legacy table names.
 */
const DATA_TYPE_LABELS: Record<SyncDataType, string> = {
	[SyncDataType.Glucose]: 'Glucose',
	[SyncDataType.ManualBG]: 'Manual BG',
	[SyncDataType.Calibrations]: 'Calibrations',
	[SyncDataType.Boluses]: 'Boluses',
	[SyncDataType.BasalInjections]: 'Basal Injections',
	[SyncDataType.CarbIntake]: 'Carb Intake',
	[SyncDataType.TempBasals]: 'Basal',
	[SyncDataType.BGChecks]: 'BG Checks',
	[SyncDataType.BolusCalculations]: 'Bolus Calculations',
	[SyncDataType.Notes]: 'Notes',
	[SyncDataType.DeviceEvents]: 'Device Events',
	[SyncDataType.StateSpans]: 'State Spans',
	[SyncDataType.Profiles]: 'Profiles',
	[SyncDataType.DeviceStatus]: 'Device Status',
	[SyncDataType.Activity]: 'Activity',
	[SyncDataType.Food]: 'Food',
};

/** Get a human-readable label for a data type key */
export function getDataTypeLabel(key: string): string {
	const label = DATA_TYPE_LABELS[key as SyncDataType];
	if (label) return label
	const legacyLabels = {

	// Legacy table keys (used when legacy tables still contain data)
	Entries: 'Entries (Legacy)',
	Treatments: 'Treatments (Legacy)',
	}
	if (key in legacyLabels) return legacyLabels[key as keyof typeof legacyLabels];
	return key;
}
