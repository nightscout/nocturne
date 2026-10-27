import { DeviceEventType } from '#lib/api/index.js';
import { isOneOf } from '#lib/utils/type-guards.js';

const DEVICE_EVENT_TYPE_VALUES: readonly DeviceEventType[] = Object.values(DeviceEventType);

/** Human-readable labels for DeviceEventType enum values. */
export const DEVICE_EVENT_TYPE_LABELS: Record<DeviceEventType, string> = {
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

/** All DeviceEventType values in display order. */
export const DEVICE_EVENT_TYPES = Object.keys(DEVICE_EVENT_TYPE_LABELS).filter(
	(key): key is DeviceEventType => isOneOf(DEVICE_EVENT_TYPE_VALUES, key)
);

/** Get the human-readable label for a device event type, falling back to the raw value. */
export function getDeviceEventTypeLabel(type: string | undefined): string {
	if (!type) return '';
	return isOneOf(DEVICE_EVENT_TYPE_VALUES, type) ? DEVICE_EVENT_TYPE_LABELS[type] : type;
}
