/**
 * The API's chart enums as string unions. The app's generated enums are assignable to these,
 * so a member the API adds and these lack fails the app's type check rather than drawing wrong.
 */

export const BasalDeliveryOrigin = {
	Algorithm: 'Algorithm',
	Scheduled: 'Scheduled',
	Manual: 'Manual',
	Suspended: 'Suspended',
	Inferred: 'Inferred',
} as const;
export type BasalDeliveryOrigin = (typeof BasalDeliveryOrigin)[keyof typeof BasalDeliveryOrigin];

export const BolusType2 = {
	Bolus: 'Bolus',
	MealBolus: 'MealBolus',
	CorrectionBolus: 'CorrectionBolus',
	SnackBolus: 'SnackBolus',
	BolusWizard: 'BolusWizard',
	ComboBolus: 'ComboBolus',
	Smb: 'Smb',
	AutomaticBolus: 'AutomaticBolus',
} as const;
export type BolusType2 = (typeof BolusType2)[keyof typeof BolusType2];

export const SystemEventType = {
	Alarm: 'Alarm',
	Hazard: 'Hazard',
	Warning: 'Warning',
	Info: 'Info',
} as const;
export type SystemEventType = (typeof SystemEventType)[keyof typeof SystemEventType];

export const SystemEventCategory = {
	Pump: 'Pump',
	Cgm: 'Cgm',
	Connectivity: 'Connectivity',
} as const;
export type SystemEventCategory = (typeof SystemEventCategory)[keyof typeof SystemEventCategory];

export const DeviceEventType = {
	SensorStart: 'SensorStart',
	SensorChange: 'SensorChange',
	SensorStop: 'SensorStop',
	SiteChange: 'SiteChange',
	InsulinChange: 'InsulinChange',
	PumpBatteryChange: 'PumpBatteryChange',
	PodChange: 'PodChange',
	ReservoirChange: 'ReservoirChange',
	CannulaChange: 'CannulaChange',
	TransmitterSensorInsert: 'TransmitterSensorInsert',
	PodActivated: 'PodActivated',
	PodDeactivated: 'PodDeactivated',
	PumpSuspend: 'PumpSuspend',
	PumpResume: 'PumpResume',
	Priming: 'Priming',
	TubePriming: 'TubePriming',
	NeedlePriming: 'NeedlePriming',
	Rewind: 'Rewind',
	DateChanged: 'DateChanged',
	TimeChanged: 'TimeChanged',
	BolusMaxChanged: 'BolusMaxChanged',
	BasalMaxChanged: 'BasalMaxChanged',
	ProfileSwitch: 'ProfileSwitch',
} as const;
export type DeviceEventType = (typeof DeviceEventType)[keyof typeof DeviceEventType];

/** The legacy Nightscout event-type strings a device event may still carry. */
export const NightscoutDeviceEventType = {
	SiteChange: 'Site Change',
	SensorStart: 'Sensor Start',
	SensorChange: 'Sensor Change',
	SensorStop: 'Sensor Stop',
	PumpBatteryChange: 'Pump Battery Change',
	InsulinChange: 'Insulin Change',
} as const;

export const TrackerCategory = {
	Consumable: 'Consumable',
	Reservoir: 'Reservoir',
	Appointment: 'Appointment',
	Reminder: 'Reminder',
	Custom: 'Custom',
	Sensor: 'Sensor',
	Cannula: 'Cannula',
	Battery: 'Battery',
	Pod: 'Pod',
} as const;
export type TrackerCategory = (typeof TrackerCategory)[keyof typeof TrackerCategory];

export const StateSpanCategory = {
	PumpMode: 'PumpMode',
	PumpConnectivity: 'PumpConnectivity',
	Override: 'Override',
	Profile: 'Profile',
	Exercise: 'Exercise',
	Illness: 'Illness',
	Travel: 'Travel',
	DataExclusion: 'DataExclusion',
	TemporaryTarget: 'TemporaryTarget',
	Motion: 'Motion',
} as const;
export type StateSpanCategory = (typeof StateSpanCategory)[keyof typeof StateSpanCategory];

export const ChartSpanKind = {
	StateSpan: 'StateSpan',
	Sleep: 'Sleep',
} as const;
export type ChartSpanKind = (typeof ChartSpanKind)[keyof typeof ChartSpanKind];

export const MealPeriod = {
	Breakfast: 'Breakfast',
	Lunch: 'Lunch',
	Snack: 'Snack',
	Dinner: 'Dinner',
	LateNight: 'LateNight',
} as const;
export type MealPeriod = (typeof MealPeriod)[keyof typeof MealPeriod];

export const PumpModeState = {
	Automatic: 'Automatic',
	Limited: 'Limited',
	Manual: 'Manual',
	Boost: 'Boost',
	EaseOff: 'EaseOff',
	Sleep: 'Sleep',
	Exercise: 'Exercise',
	Liberty: 'Liberty',
	Suspended: 'Suspended',
	Off: 'Off',
} as const;
export type PumpModeState = (typeof PumpModeState)[keyof typeof PumpModeState];

/** The API's `ChartColor` values: each names the CSS custom property `themes/nocturne.css` or the host defines. */
export const CHART_COLORS = [
	'glucose-very-low',
	'glucose-low',
	'glucose-in-range',
	'glucose-high',
	'glucose-very-high',
	'insulin-bolus',
	'insulin-basal',
	'insulin-temp-basal',
	'carbs',
	'pump-mode-automatic',
	'pump-mode-limited',
	'pump-mode-manual',
	'pump-mode-boost',
	'pump-mode-ease-off',
	'pump-mode-sleep',
	'pump-mode-exercise',
	'pump-mode-suspended',
	'pump-mode-off',
	'pump-mode-liberty',
	'system-event-alarm',
	'system-event-hazard',
	'system-event-warning',
	'system-event-info',
	'activity-sleep',
	'activity-exercise',
	'activity-illness',
	'activity-travel',
	'tracker-sensor',
	'tracker-cannula',
	'tracker-pod',
	'tracker-reservoir',
	'tracker-battery',
	'tracker-consumable',
	'tracker-appointment',
	'tracker-reminder',
	'tracker-custom',
	'heart-rate',
	'steps',
	'chart-1',
	'chart-2',
	'muted-foreground',
	'primary',
] as const;
export type ChartColor = (typeof CHART_COLORS)[number];

const chartColors: ReadonlySet<string> = new Set(CHART_COLORS);

/** Whether a value from the API is one of the theme colours, so it may name a CSS custom property. */
export function isChartColor(value: unknown): value is ChartColor {
	return typeof value === 'string' && chartColors.has(value);
}
