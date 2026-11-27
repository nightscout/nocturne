import { SyncDataType } from '@nocturne/api-client';

/**
 * Heading labels for the record type keys the API returns, keyed by SyncDataType so every type has one.
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

/** Lower-case plural forms that read inside a sentence, such as "12 glucose readings". */
const DATA_TYPE_COUNT_LABELS: Record<SyncDataType, string> = {
	[SyncDataType.Glucose]: 'glucose readings',
	[SyncDataType.ManualBG]: 'manual BG readings',
	[SyncDataType.Calibrations]: 'calibrations',
	[SyncDataType.Boluses]: 'boluses',
	[SyncDataType.BasalInjections]: 'basal injections',
	[SyncDataType.CarbIntake]: 'carb intakes',
	[SyncDataType.TempBasals]: 'temp basals',
	[SyncDataType.BGChecks]: 'BG checks',
	[SyncDataType.BolusCalculations]: 'bolus calculations',
	[SyncDataType.Notes]: 'notes',
	[SyncDataType.DeviceEvents]: 'device events',
	[SyncDataType.StateSpans]: 'state spans',
	[SyncDataType.Profiles]: 'profile records',
	[SyncDataType.DeviceStatus]: 'device status records',
	[SyncDataType.Activity]: 'activity records',
	[SyncDataType.Food]: 'food records',
	[SyncDataType.Steps]: 'step counts',
	[SyncDataType.HeartRate]: 'heart rate readings',
	[SyncDataType.BodyWeight]: 'body weights',
	[SyncDataType.Sleep]: 'sleep sessions',
};

/** Table keys used while legacy tables still contain data */
const LEGACY_TABLE_LABELS: Record<string, { heading: string; count: string }> = {
	Entries: { heading: 'Entries (Legacy)', count: 'legacy entries' },
	Treatments: { heading: 'Treatments (Legacy)', count: 'legacy treatments' },
};

function labelsByKey(
	labels: Record<SyncDataType, string>,
	legacy: (label: { heading: string; count: string }) => string,
): ReadonlyMap<string, string> {
	return new Map([
		...Object.entries(labels),
		...Object.entries(LEGACY_TABLE_LABELS).map(
			([key, label]) => [key, legacy(label)] as const,
		),
	]);
}

const HEADINGS_BY_KEY = labelsByKey(DATA_TYPE_LABELS, (l) => l.heading);
const COUNT_LABELS_BY_KEY = labelsByKey(DATA_TYPE_COUNT_LABELS, (l) => l.count);

/** Get a human-readable label for a data type key */
export function getDataTypeLabel(key: string): string {
	return HEADINGS_BY_KEY.get(key) || key;
}

/** Get the lower-case plural label for a data type key, for use after a count */
export function getDataTypeCountLabel(key: string): string {
	return COUNT_LABELS_BY_KEY.get(key) || key;
}
