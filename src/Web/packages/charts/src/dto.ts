import type {
	BasalDeliveryOrigin,
	BolusType2,
	ChartColor,
	ChartSpanKind,
	DeviceEventType,
	MealPeriod,
	StateSpanCategory,
	SystemEventCategory,
	SystemEventType,
	TrackerCategory,
} from './enums.js';

/**
 * The API's `DashboardChartData` and its parts, as far as the chart reads them. The app's
 * generated types are assignable to these; a host without the generated client builds them.
 */
export interface ChartThresholdsDto {
	low?: number;
	high?: number;
	veryLow?: number;
	veryHigh?: number;
	glucoseYMax?: number;
	targetLow?: number | undefined;
	targetHigh?: number | undefined;
}

export interface TimeSeriesPointDto {
	timestamp?: number;
	value?: number;
}

export interface BasalPointDto {
	timestamp?: number;
	rate?: number;
	scheduledRate?: number | undefined;
	origin?: BasalDeliveryOrigin;
	fillColor?: ChartColor;
	strokeColor?: ChartColor;
}

export interface GlucosePointDto {
	time?: number;
	sgv?: number;
	direction?: string | undefined;
	dataSource?: string | undefined;
}

export interface BolusMarkerDto {
	time?: number;
	insulin?: number;
	treatmentId?: string | undefined;
	bolusType?: BolusType2;
	isOverride?: boolean;
	dataSource?: string | undefined;
}

export interface CarbMarkerDto {
	time?: number;
	carbs?: number;
	label?: string | undefined;
	mealPeriod?: MealPeriod | undefined;
	treatmentId?: string | undefined;
	isOffset?: boolean;
	dataSource?: string | undefined;
}

export interface DeviceEventMarkerDto {
	time?: number;
	eventType?: DeviceEventType;
	notes?: string | undefined;
	treatmentId?: string | undefined;
	color?: ChartColor;
	isInferred?: boolean;
}

export interface BasalInjectionMarkerDto {
	id?: string;
	time?: number;
	units?: number;
	insulinName?: string | undefined;
}

export interface BgCheckMarkerDto {
	time?: number;
	glucose?: number;
	glucoseType?: string | undefined;
	treatmentId?: string | undefined;
}

export interface ProfileActivationDto {
	id?: string;
	baseProfileName?: string;
	percentage?: number;
	timeshiftHours?: number;
	phase?: string;
	origin?: string;
	app?: string | undefined;
	dataSource?: string | undefined;
	notes?: string | undefined;
	enteredBy?: string | undefined;
	isTemporary?: boolean;
	startMills?: number;
	endMills?: number | undefined;
}

export interface ChartStateSpanDto {
	id?: string;
	kind?: ChartSpanKind;
	category?: StateSpanCategory | undefined;
	state?: string;
	startMills?: number;
	endMills?: number | undefined;
	color?: ChartColor;
	metadata?: { [key: string]: unknown } | undefined;
	profileActivation?: ProfileActivationDto | undefined;
}

export interface BasalDeliverySpanDto {
	id?: string;
	startMills?: number;
	endMills?: number | undefined;
	rate?: number;
	origin?: BasalDeliveryOrigin;
	source?: string | undefined;
	fillColor?: ChartColor;
	strokeColor?: ChartColor;
}

export interface SystemEventMarkerDto {
	id?: string;
	time?: number;
	eventType?: SystemEventType;
	category?: SystemEventCategory;
	code?: string | undefined;
	description?: string | undefined;
	color?: ChartColor;
}

export interface TrackerMarkerDto {
	id?: string;
	definitionId?: string;
	name?: string;
	category?: TrackerCategory;
	time?: number;
	icon?: string | undefined;
	color?: ChartColor;
}

export interface DashboardChartDataDto {
	iobSeries?: TimeSeriesPointDto[];
	cobSeries?: TimeSeriesPointDto[];
	basalSeries?: BasalPointDto[];
	defaultBasalRate?: number;
	maxBasalRate?: number;
	minIob?: number;
	maxIob?: number;
	maxCob?: number;
	glucoseData?: GlucosePointDto[];
	thresholds?: ChartThresholdsDto;
	bolusMarkers?: BolusMarkerDto[];
	carbMarkers?: CarbMarkerDto[];
	deviceEventMarkers?: DeviceEventMarkerDto[];
	bgCheckMarkers?: BgCheckMarkerDto[];
	pumpModeSpans?: ChartStateSpanDto[];
	profileSpans?: ChartStateSpanDto[];
	overrideSpans?: ChartStateSpanDto[];
	activitySpans?: ChartStateSpanDto[];
	tempBasalSpans?: ChartStateSpanDto[];
	basalDeliverySpans?: BasalDeliverySpanDto[];
	lastPumpSyncMills?: number | undefined;
	basalInjectionMarkers?: BasalInjectionMarkerDto[];
	systemEventMarkers?: SystemEventMarkerDto[];
	trackerMarkers?: TrackerMarkerDto[];
}
