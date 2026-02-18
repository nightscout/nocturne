/**
 * Status Pills UI Types
 *
 * UI-specific types for displaying status information in pill format.
 * Data types (DeviceStatus, Bolus, CarbIntake, DeviceEvent, Profile, etc.)
 * come from the backend via the generated API client in $lib/api.
 *
 * @see $lib/api for backend types: DeviceStatus, LoopStatus, PumpStatus,
 *      OpenApsStatus, LoopIob, LoopCob, Bolus, CarbIntake, DeviceEvent, Profile, etc.
 */

/**
 * Alert levels for visual styling of pills
 * Maps to Nightscout's severity levels: none, info, warn, urgent
 */
export type AlertLevel = 'none' | 'info' | 'warn' | 'urgent';

/**
 * Key-value item displayed in pill popover
 */
export interface PillInfoItem {
	label: string;
	value: string | number;
}

/**
 * Base display properties for all status pills
 * Contains only UI-specific properties, not data
 */
export interface PillDisplayProps {
	/** Formatted display value shown in the pill */
	display: string;
	/** Label for the pill (IOB, COB, CAGE, etc.) */
	label: string;
	/** Array of info items for popover */
	info: PillInfoItem[];
	/** Alert level for styling */
	level: AlertLevel;
	/** Whether the data is stale/outdated */
	isStale?: boolean;
	/** Timestamp when data was last updated (milliseconds) */
	lastUpdated?: number;
}

/**
 * IOB pill display data
 */
export interface IOBPillData extends PillDisplayProps {
	/** Current IOB value in units */
	iob: number;
	/** Basal IOB component */
	basalIob?: number;
	/** Insulin activity */
	activity?: number;
	/** Source of IOB data */
	source?: string;
	/** Device providing the data */
	device?: string;
	/** Last bolus info */
	lastBolus?: {
		mills: number;
		insulin: number;
		notes?: string;
	};
}

/**
 * COB pill display data
 */
export interface COBPillData extends PillDisplayProps {
	/** Current COB value in grams */
	cob: number;
	/** Source of COB data */
	source?: string;
	/** Whether COB is actively decaying */
	isDecaying?: boolean;
	/** Last carbs info */
	lastCarbs?: {
		mills: number;
		carbs: number;
		food?: string;
		notes?: string;
	};
}

/**
 * CAGE (Cannula Age) pill display data
 */
export interface CAGEPillData extends PillDisplayProps {
	/** Age in hours */
	age: number;
	/** Age in days */
	days: number;
	/** Remaining hours after full days */
	hours: number;
	/** Time remaining before change needed (hours) */
	timeRemaining?: number;
	/** Timestamp of the device event (e.g., site change) */
	eventDate: number;
	/** Notes from the device event */
	notes?: string;
}

/**
 * SAGE (Sensor Age) pill display data
 */
export interface SAGEPillData extends PillDisplayProps {
	/** Age in hours */
	age: number;
	/** Age in days */
	days: number;
	/** Remaining hours after full days */
	hours: number;
	/** Time remaining before change needed (hours) */
	timeRemaining?: number;
	/** Timestamp of the device event (e.g., sensor start/change) */
	eventDate: number;
	/** Notes from the device event */
	notes?: string;
	/** Transmitter ID from treatment */
	transmitterId?: string;
	/** Event type */
	eventType?: string;
}

/**
 * Basal pill display data
 */
export interface BasalPillData extends PillDisplayProps {
	/** Current total basal rate */
	totalBasal: number;
	/** Scheduled basal rate */
	scheduledBasal: number;
	/** Whether a temp basal is active */
	isTempBasal: boolean;
	/** Whether combo bolus is affecting basal */
	isComboActive: boolean;
	/** Active profile name */
	activeProfile?: string;
	/** Temp basal details */
	tempBasal?: {
		rate?: number;
		percent?: number;
		duration: number;
		remaining: number;
		startTime: number;
	};
}

/**
 * Loop status display codes
 */
export type LoopStatusCode = 'enacted' | 'recommendation' | 'looping' | 'warning' | 'error';

/**
 * Loop pill display data
 */
export interface LoopPillData extends PillDisplayProps {
	/** Status code */
	status: LoopStatusCode;
	/** Status symbol for display */
	symbol: string;
	/** Last loop timestamp */
	lastLoopTime?: number;
	/** Last successful loop timestamp */
	lastOkTime?: number;
	/** Loop name (Loop, OpenAPS, etc.) */
	loopName?: string;
	/** Eventual BG prediction */
	eventualBG?: number;
	/** IOB from loop */
	iob?: number;
	/** COB from loop */
	cob?: number;
	/** Failure reason if error */
	failureReason?: string;
	/** Last enacted action */
	lastEnacted?: {
		time: number;
		type: 'temp_basal' | 'bolus' | 'cancel';
		rate?: number;
		duration?: number;
		bolusVolume?: number;
		reason?: string;
	};
}

/**
 * All possible pill types as a union
 */
export type PillType = 'iob' | 'cob' | 'cage' | 'sage' | 'iage' | 'bage' | 'basal' | 'loop' | 'pump' | 'upbat';

/**
 * Configuration for status pills thresholds and display options
 */
export interface StatusPillsConfig {
	/** Enabled pills in order */
	enabledPills: PillType[];
	/** COB configuration */
	cob?: {
		/** Recency threshold in minutes */
		recencyThreshold?: number;
	};
	/** IOB configuration */
	iob?: {
		/** Recency threshold in minutes */
		recencyThreshold?: number;
	};
	/** CAGE configuration */
	cage?: {
		/** Hours until info alert */
		infoThreshold?: number;
		/** Hours until warning */
		warnThreshold?: number;
		/** Hours until urgent */
		urgentThreshold?: number;
		/** Display format */
		displayFormat?: 'hours' | 'days';
		/** Enable alerts */
		enableAlerts?: boolean;
	};
	/** SAGE configuration */
	sage?: {
		/** Hours until info alert */
		infoThreshold?: number;
		/** Hours until warning */
		warnThreshold?: number;
		/** Hours until urgent */
		urgentThreshold?: number;
		/** Enable alerts */
		enableAlerts?: boolean;
	};
	/** Loop configuration */
	loop?: {
		/** Minutes until warning */
		warnThreshold?: number;
		/** Minutes until urgent */
		urgentThreshold?: number;
		/** Enable alerts */
		enableAlerts?: boolean;
	};
}

/**
 * Default configuration values (matching Nightscout defaults)
 */
export const DEFAULT_PILLS_CONFIG: StatusPillsConfig = {
	enabledPills: ['iob', 'cob', 'cage', 'sage', 'basal', 'loop'],
	cob: {
		recencyThreshold: 30
	},
	iob: {
		recencyThreshold: 30
	},
	cage: {
		infoThreshold: 44,
		warnThreshold: 48,
		urgentThreshold: 72,
		displayFormat: 'hours',
		enableAlerts: false
	},
	sage: {
		infoThreshold: 144, // 6 days
		warnThreshold: 164, // 7 days - 4 hours
		urgentThreshold: 166, // 7 days - 2 hours
		enableAlerts: false
	},
	loop: {
		warnThreshold: 30,
		urgentThreshold: 60,
		enableAlerts: false
	}
};
