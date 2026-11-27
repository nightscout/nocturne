import { SyncDataType } from '@nocturne/api-client';
import { plural } from './plural.js';

/** Every record type a connector syncs, and the tables that hold legacy data. */
type DataTypeKey = SyncDataType | 'Entries' | 'Treatments';

/** The record types a connector syncs, as headings: "Glucose". Built per call, as `scopeLabels` is. */
function dataTypeLabels(): Record<DataTypeKey, string> {
	return {
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
		Entries: 'Entries (Legacy)',
		Treatments: 'Treatments (Legacy)',
	};
}

/** The record types inside a sentence, in the plural: "12 glucose readings". Built per call. */
function dataTypePhrases(): Record<DataTypeKey, string> {
	return {
		// @wc-include
		[SyncDataType.Glucose]: 'glucose readings',
		// @wc-include
		[SyncDataType.ManualBG]: 'manual BG readings',
		// @wc-include
		[SyncDataType.Calibrations]: 'calibrations',
		// @wc-include
		[SyncDataType.Boluses]: 'boluses',
		// @wc-include
		[SyncDataType.BasalInjections]: 'basal injections',
		// @wc-include
		[SyncDataType.CarbIntake]: 'carb intakes',
		// @wc-include
		[SyncDataType.TempBasals]: 'temp basals',
		// @wc-include
		[SyncDataType.BGChecks]: 'BG checks',
		// @wc-include
		[SyncDataType.BolusCalculations]: 'bolus calculations',
		// @wc-include
		[SyncDataType.Notes]: 'notes',
		// @wc-include
		[SyncDataType.DeviceEvents]: 'device events',
		// @wc-include
		[SyncDataType.StateSpans]: 'state spans',
		// @wc-include
		[SyncDataType.Profiles]: 'profile records',
		// @wc-include
		[SyncDataType.DeviceStatus]: 'device status records',
		// @wc-include
		[SyncDataType.Activity]: 'activity records',
		// @wc-include
		[SyncDataType.Food]: 'food records',
		// @wc-include
		[SyncDataType.Steps]: 'step counts',
		// @wc-include
		[SyncDataType.HeartRate]: 'heart rate readings',
		// @wc-include
		[SyncDataType.BodyWeight]: 'body weights',
		// @wc-include
		[SyncDataType.Sleep]: 'sleep sessions',
		// @wc-include
		Entries: 'legacy entries',
		// @wc-include
		Treatments: 'legacy treatments',
	};
}

/** The record types after a count, in the form the count takes: "1 glucose reading", "12 glucose readings". */
function dataTypeCounts(): Record<DataTypeKey, (count: number) => string> {
	return {
		[SyncDataType.Glucose]: (n) => plural(n, ['# glucose reading', '# glucose readings']),
		[SyncDataType.ManualBG]: (n) => plural(n, ['# manual BG reading', '# manual BG readings']),
		[SyncDataType.Calibrations]: (n) => plural(n, ['# calibration', '# calibrations']),
		[SyncDataType.Boluses]: (n) => plural(n, ['# bolus', '# boluses']),
		[SyncDataType.BasalInjections]: (n) => plural(n, ['# basal injection', '# basal injections']),
		[SyncDataType.CarbIntake]: (n) => plural(n, ['# carb intake', '# carb intakes']),
		[SyncDataType.TempBasals]: (n) => plural(n, ['# temp basal', '# temp basals']),
		[SyncDataType.BGChecks]: (n) => plural(n, ['# BG check', '# BG checks']),
		[SyncDataType.BolusCalculations]: (n) => plural(n, ['# bolus calculation', '# bolus calculations']),
		[SyncDataType.Notes]: (n) => plural(n, ['# note', '# notes']),
		[SyncDataType.DeviceEvents]: (n) => plural(n, ['# device event', '# device events']),
		[SyncDataType.StateSpans]: (n) => plural(n, ['# state span', '# state spans']),
		[SyncDataType.Profiles]: (n) => plural(n, ['# profile record', '# profile records']),
		[SyncDataType.DeviceStatus]: (n) => plural(n, ['# device status record', '# device status records']),
		[SyncDataType.Activity]: (n) => plural(n, ['# activity record', '# activity records']),
		[SyncDataType.Food]: (n) => plural(n, ['# food record', '# food records']),
		[SyncDataType.Steps]: (n) => plural(n, ['# step count', '# step counts']),
		[SyncDataType.HeartRate]: (n) => plural(n, ['# heart rate reading', '# heart rate readings']),
		[SyncDataType.BodyWeight]: (n) => plural(n, ['# body weight', '# body weights']),
		[SyncDataType.Sleep]: (n) => plural(n, ['# sleep session', '# sleep sessions']),
		Entries: (n) => plural(n, ['# legacy entry', '# legacy entries']),
		Treatments: (n) => plural(n, ['# legacy treatment', '# legacy treatments']),
	};
}

function wordOf(words: Readonly<Record<string, string>>, key: string): string {
	return Object.hasOwn(words, key) ? words[key] : key;
}

/** The heading for a record type key; the key itself when the web has no word for it. */
export function dataTypeLabel(key: string): string {
	return wordOf(dataTypeLabels(), key);
}

/** The plural noun for a record type key, for use inside a sentence or after a count. */
export function dataTypePhrase(key: string): string {
	return wordOf(dataTypePhrases(), key);
}

/** A count of a record type inside a sentence; the plural noun with the number when the web has no word for the key. */
export function dataTypeCount(key: string, count: number): string {
	const counts: Readonly<Record<string, (count: number) => string>> = dataTypeCounts();
	return Object.hasOwn(counts, key) ? counts[key](count) : `${count} ${key}`;
}
