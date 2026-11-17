import { DeviceEventType } from '#lib/api/index.js';
import { isOneOf } from '#lib/utils/type-guards.js';

/** All DeviceEventType values in display order. */
export const DEVICE_EVENT_TYPES: readonly DeviceEventType[] = [
	DeviceEventType.SensorStart,
	DeviceEventType.SensorChange,
	DeviceEventType.SensorStop,
	DeviceEventType.SiteChange,
	DeviceEventType.InsulinChange,
	DeviceEventType.PumpBatteryChange,
	DeviceEventType.PodChange,
	DeviceEventType.ReservoirChange,
	DeviceEventType.CannulaChange,
	DeviceEventType.TransmitterSensorInsert,
	DeviceEventType.PodActivated,
	DeviceEventType.PodDeactivated,
	DeviceEventType.PumpSuspend,
	DeviceEventType.PumpResume,
	DeviceEventType.Priming,
	DeviceEventType.TubePriming,
	DeviceEventType.NeedlePriming,
	DeviceEventType.Rewind,
	DeviceEventType.DateChanged,
	DeviceEventType.TimeChanged,
	DeviceEventType.BolusMaxChanged,
	DeviceEventType.BasalMaxChanged,
	DeviceEventType.ProfileSwitch,
];

/** Built per call so wuchale reads them in the current locale. */
function deviceEventTypeLabels(): Record<DeviceEventType, string> {
	return {
		[DeviceEventType.SensorStart]: 'Sensor start',
		[DeviceEventType.SensorChange]: 'Sensor change',
		[DeviceEventType.SensorStop]: 'Sensor stop',
		[DeviceEventType.SiteChange]: 'Site change',
		[DeviceEventType.InsulinChange]: 'Insulin change',
		[DeviceEventType.PumpBatteryChange]: 'Pump battery change',
		[DeviceEventType.PodChange]: 'Pod change',
		[DeviceEventType.ReservoirChange]: 'Reservoir change',
		[DeviceEventType.CannulaChange]: 'Cannula change',
		[DeviceEventType.TransmitterSensorInsert]: 'Transmitter / Sensor Insert',
		[DeviceEventType.PodActivated]: 'Pod activated',
		[DeviceEventType.PodDeactivated]: 'Pod deactivated',
		[DeviceEventType.PumpSuspend]: 'Pump suspend',
		[DeviceEventType.PumpResume]: 'Pump resume',
		[DeviceEventType.Priming]: 'Priming',
		[DeviceEventType.TubePriming]: 'Tube priming',
		[DeviceEventType.NeedlePriming]: 'Needle priming',
		[DeviceEventType.Rewind]: 'Rewind',
		[DeviceEventType.DateChanged]: 'Date changed',
		[DeviceEventType.TimeChanged]: 'Time changed',
		[DeviceEventType.BolusMaxChanged]: 'Bolus max changed',
		[DeviceEventType.BasalMaxChanged]: 'Basal max changed',
		[DeviceEventType.ProfileSwitch]: 'Profile switch',
	};
}

/** Get the human-readable label for a device event type, falling back to the raw value. */
export function getDeviceEventTypeLabel(type: string | undefined): string {
	if (!type) return '';
	return isOneOf(DEVICE_EVENT_TYPES, type) ? deviceEventTypeLabels()[type] : type;
}

/**
 * The label for a Nightscout treatment event name (`Sensor Start`), which is how the tracker
 * endpoints name device events. Each of those names is its DeviceEventType member with spaces
 * between the words; a name with no such member is shown as received.
 */
export function getNightscoutEventTypeLabel(name: string | undefined): string {
	if (!name) return '';
	const type = name.replaceAll(' ', '');
	return isOneOf(DEVICE_EVENT_TYPES, type) ? deviceEventTypeLabels()[type] : name;
}
