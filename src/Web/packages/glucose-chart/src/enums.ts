export const BasalDeliveryOrigin = {
	Algorithm: 'Algorithm',
	Scheduled: 'Scheduled',
	Manual: 'Manual',
	Suspended: 'Suspended',
	Inferred: 'Inferred',
} as const;
export type BasalDeliveryOrigin = typeof BasalDeliveryOrigin[keyof typeof BasalDeliveryOrigin];

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
export type BolusType2 = typeof BolusType2[keyof typeof BolusType2];

export const SystemEventType = {
	Alarm: 'Alarm',
	Hazard: 'Hazard',
	Warning: 'Warning',
	Info: 'Info',
} as const;
export type SystemEventType = typeof SystemEventType[keyof typeof SystemEventType];

export const SystemEventCategory = {
	Pump: 'Pump',
	Cgm: 'Cgm',
	Connectivity: 'Connectivity',
} as const;
export type SystemEventCategory = typeof SystemEventCategory[keyof typeof SystemEventCategory];

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
export type DeviceEventType = typeof DeviceEventType[keyof typeof DeviceEventType];

export const TrackerCategory = {
	Consumable: 'Consumable',
	Reservoir: 'Reservoir',
	Appointment: 'Appointment',
	Reminder: 'Reminder',
	Custom: 'Custom',
	Sensor: 'Sensor',
	Cannula: 'Cannula',
	Battery: 'Battery',
} as const;
export type TrackerCategory = typeof TrackerCategory[keyof typeof TrackerCategory];

export const StateSpanCategory = {
	PumpMode: 'PumpMode',
	PumpConnectivity: 'PumpConnectivity',
	Override: 'Override',
	Profile: 'Profile',
	Sleep: 'Sleep',
	Exercise: 'Exercise',
	Illness: 'Illness',
	Travel: 'Travel',
	DataExclusion: 'DataExclusion',
	TemporaryTarget: 'TemporaryTarget',
} as const;
export type StateSpanCategory = typeof StateSpanCategory[keyof typeof StateSpanCategory];

export const ChartSpanKind = {
	StateSpan: 'StateSpan',
	Sleep: 'Sleep',
} as const;
export type ChartSpanKind = typeof ChartSpanKind[keyof typeof ChartSpanKind];

export const CalculationType2 = {
	Suggested: 'Suggested',
	Manual: 'Manual',
	Automatic: 'Automatic',
} as const;
export type CalculationType2 = typeof CalculationType2[keyof typeof CalculationType2];
