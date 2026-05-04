import type {
	BasalDeliveryOrigin,
	BolusType2,
	DeviceEventType,
	SystemEventType,
	SystemEventCategory,
	TrackerCategory,
	StateSpanCategory,
} from './enums.js';

export interface ChartStateSpan {
	id?: string;
	category?: StateSpanCategory;
	state?: string;
	startTime: Date;
	endTime: Date | null;
	color: string;
	metadata?: Record<string, unknown> | null;
}

export interface TransformedChartData {
	iobSeries: Array<{ time: Date; value: number }>;
	cobSeries: Array<{ time: Date; value: number }>;
	basalSeries: Array<{
		timestamp?: number;
		rate?: number;
		scheduledRate?: number;
		origin?: BasalDeliveryOrigin;
		fillColor: string;
		strokeColor: string;
	}>;
	defaultBasalRate: number;
	maxBasalRate: number;
	maxIob: number;
	maxCob: number;
	glucoseData: Array<{
		time: Date;
		sgv: number;
		direction?: string | null;
		dataSource?: string | null;
		color: string;
	}>;
	thresholds: {
		low: number;
		high: number;
		veryLow: number;
		veryHigh: number;
		glucoseYMax: number;
	};
	bolusMarkers: Array<{
		time: Date;
		insulin?: number;
		treatmentId?: string | null;
		bolusType?: BolusType2;
		isOverride?: boolean;
		dataSource?: string | null;
	}>;
	carbMarkers: Array<{
		time: Date;
		carbs?: number;
		label?: string | null;
		treatmentId?: string | null;
		isOffset?: boolean;
		dataSource?: string | null;
	}>;
	deviceEventMarkers: Array<{
		time: Date;
		eventType?: DeviceEventType;
		notes?: string | null;
		treatmentId?: string | null;
		color: string;
	}>;
	systemEventMarkers: Array<{
		time: Date;
		id?: string;
		eventType?: SystemEventType;
		category?: SystemEventCategory;
		code?: string | null;
		description?: string | null;
		color: string;
	}>;
	trackerMarkers: Array<{
		time: Date;
		id?: string;
		definitionId?: string;
		name?: string;
		category?: TrackerCategory;
		icon?: string | null;
		color: string;
	}>;
	pumpModeSpans: ChartStateSpan[];
	profileSpans: ChartStateSpan[];
	overrideSpans: ChartStateSpan[];
	activitySpans: ChartStateSpan[];
	tempBasalSpans: ChartStateSpan[];
	basalDeliverySpans: Array<{
		id?: string;
		startMills?: number;
		endMills?: number | null;
		rate?: number;
		origin?: BasalDeliveryOrigin;
		source?: string | null;
		startTime: Date;
		endTime: Date | null;
		fillColor: string;
		strokeColor: string;
	}>;
	heartRateSeries: Array<{ time: Date; bpm: number }>;
	stepSeries: Array<{ time: Date; steps: number }>;
}
