import { DeviceEventType } from '$lib/api';

/** Human-readable labels for DeviceEventType enum values. */
export const DEVICE_EVENT_TYPE_LABELS: Record<DeviceEventType, string> = {
	[DeviceEventType.SensorStart]: 'Sensor Start',
	[DeviceEventType.SensorChange]: 'Sensor Change',
	[DeviceEventType.SensorStop]: 'Sensor Stop',
	[DeviceEventType.SiteChange]: 'Site Change',
	[DeviceEventType.InsulinChange]: 'Insulin Change',
	[DeviceEventType.PumpBatteryChange]: 'Pump Battery Change',
	[DeviceEventType.PodChange]: 'Pod Change',
	[DeviceEventType.ReservoirChange]: 'Reservoir Change',
	[DeviceEventType.CannulaChange]: 'Cannula Change',
	[DeviceEventType.TransmitterSensorInsert]: 'Transmitter / Sensor Insert',
	[DeviceEventType.PodActivated]: 'Pod Activated',
	[DeviceEventType.PodDeactivated]: 'Pod Deactivated',
	[DeviceEventType.PumpSuspend]: 'Pump Suspend',
	[DeviceEventType.PumpResume]: 'Pump Resume',
	[DeviceEventType.Priming]: 'Priming',
	[DeviceEventType.TubePriming]: 'Tube Priming',
	[DeviceEventType.NeedlePriming]: 'Needle Priming',
	[DeviceEventType.Rewind]: 'Rewind',
	[DeviceEventType.DateChanged]: 'Date Changed',
	[DeviceEventType.TimeChanged]: 'Time Changed',
	[DeviceEventType.BolusMaxChanged]: 'Bolus Max Changed',
	[DeviceEventType.BasalMaxChanged]: 'Basal Max Changed',
	[DeviceEventType.ProfileSwitch]: 'Profile Switch',
};

/** All DeviceEventType values in display order. */
export const DEVICE_EVENT_TYPES = Object.keys(DEVICE_EVENT_TYPE_LABELS) as DeviceEventType[];

/** Get the human-readable label for a device event type, falling back to the raw value. */
export function getDeviceEventTypeLabel(type: string | undefined): string {
	if (!type) return '';
	return DEVICE_EVENT_TYPE_LABELS[type as DeviceEventType] ?? type;
}
