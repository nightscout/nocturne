/**
 * Pills Data Processor
 *
 * Processes device status, v4 records, and profile data into the format
 * expected by the status pill components. This mirrors the logic from
 * Nightscout's plugins (cob.js, iob.js, cage.js, sage.js, loop.js, basal.js).
 *
 * Uses backend types (DeviceStatus, Bolus, CarbIntake, DeviceEvent, Profile)
 * from the generated API client.
 */

import type {
	COBPillData,
	IOBPillData,
	CAGEPillData,
	SAGEPillData,
	BasalPillData,
	LoopPillData,
	AlertLevel,
	StatusPillsConfig
} from '$lib/types/status-pills';
import type { Bolus, CarbIntake, DeviceEvent, DeviceStatus, Profile } from '$lib/api';
import { DeviceEventType } from '$lib/api';

// Re-export the default config
export { DEFAULT_PILLS_CONFIG } from '$lib/types/status-pills';

/**
 * Strongly typed interfaces for OpenAPS nested data structures.
 * The backend types these as `any` but we know their structure from Nightscout.
 */
interface OpenApsSuggestedEnacted {
	timestamp?: string;
	rate?: number;
	duration?: number;
	reason?: string;
	COB?: number;
	eventualBG?: number;
	received?: boolean;
}

interface OpenApsIobEntry {
	timestamp?: string;
	time?: string;
	iob?: number;
	basaliob?: number;
	activity?: number;
}

/**
 * Strongly typed interface for Loop enacted data.
 * The backend types this as `any` but we know its structure.
 */
interface LoopEnactedData {
	timestamp?: string;
	rate?: number;
	duration?: number;
	bolusVolume?: number;
	reason?: string;
	failureReason?: string;
}

/**
 * Helper to safely access OpenAPS IOB data which can be an array or single object
 */
function getOpenApsIob(iob: unknown): OpenApsIobEntry | null {
	if (!iob) return null;
	if (Array.isArray(iob)) {
		return iob[0] as OpenApsIobEntry;
	}
	return iob as OpenApsIobEntry;
}

/**
 * Time constants in milliseconds
 */
const MINUTES = (n: number) => n * 60 * 1000;
const HOURS = (n: number) => n * 60 * 60 * 1000;

/**
 * Configuration for the pills processor
 */
export interface PillsProcessorConfig extends StatusPillsConfig {
	/** Current time (for testing) */
	now?: number;
	/** BG units ('mg/dL' or 'mmol/L') */
	units?: string;
}

/**
 * Result of processing all pill data
 */
export interface ProcessedPillsData {
	iob: IOBPillData | null;
	cob: COBPillData | null;
	cage: CAGEPillData | null;
	sage: SAGEPillData | null;
	basal: BasalPillData | null;
	loop: LoopPillData | null;
}

/**
 * Process device status and v4 records into pill data
 */
export function processPillsData(
	deviceStatuses: DeviceStatus[],
	boluses: Bolus[],
	carbIntakes: CarbIntake[],
	deviceEvents: DeviceEvent[],
	profile: Profile | null,
	config: Partial<PillsProcessorConfig> = {}
): ProcessedPillsData {
	const now = config.now ?? Date.now();
	const units = config.units ?? 'mmol/L';

	return {
		iob: processIOB(deviceStatuses, boluses, now, config),
		cob: processCOB(deviceStatuses, carbIntakes, now, config),
		cage: processCAGE(deviceEvents, now, config),
		sage: processSAGE(deviceEvents, now, config),
		basal: processBasal(deviceStatuses, profile, now, units, config),
		loop: processLoop(deviceStatuses, now, units, config)
	};
}

/**
 * Process IOB data from device status and boluses
 */
export function processIOB(
	deviceStatuses: DeviceStatus[],
	boluses: Bolus[],
	now: number,
	config: Partial<PillsProcessorConfig> = {}
): IOBPillData | null {
	const recencyThreshold = MINUTES(config.iob?.recencyThreshold ?? 30);
	const futureMills = now + MINUTES(5); // Allow for clock drift
	const recentMills = now - recencyThreshold;

	// Try to get IOB from device status first (Loop, OpenAPS)
	let iobFromDevice: IOBPillData | null = null;

	const recentStatuses = deviceStatuses
		.filter((ds) => {
			const mills = ds.mills ?? (ds.created_at ? new Date(ds.created_at).getTime() : 0);
			return mills <= futureMills && mills >= recentMills;
		})
		.sort((a, b) => {
			const aMills = a.mills ?? (a.created_at ? new Date(a.created_at).getTime() : 0);
			const bMills = b.mills ?? (b.created_at ? new Date(b.created_at).getTime() : 0);
			return bMills - aMills;
		});

	for (const status of recentStatuses) {
		// Check Loop IOB
		const loopIob = status.loop?.iob;
		if (loopIob && typeof loopIob.iob === 'number') {
			const mills =
				loopIob.timestamp ? new Date(loopIob.timestamp).getTime() : status.mills ?? now;
			iobFromDevice = {
				iob: loopIob.iob,
				source: 'Loop',
				device: status.device,
				display: `${loopIob.iob.toFixed(2)}U`,
				label: 'IOB',
				info: [],
				level: 'none',
				lastUpdated: mills
			};
			break;
		}

		// Check OpenAPS IOB
		const openapsIob = status.openaps?.iob;
		if (openapsIob) {
			const iobData = getOpenApsIob(openapsIob);
			if (iobData && typeof iobData.iob === 'number') {
				const mills = iobData.timestamp
					? new Date(iobData.timestamp).getTime()
					: iobData.time
						? new Date(iobData.time).getTime()
						: status.mills ?? now;
				iobFromDevice = {
					iob: iobData.iob,
					basalIob: iobData.basaliob,
					activity: iobData.activity,
					source: 'OpenAPS',
					device: status.device,
					display: `${iobData.iob.toFixed(2)}U`,
					label: 'IOB',
					info: [],
					level: 'none',
					lastUpdated: mills
				};
				break;
			}
		}

		// Check pump IOB
		const pumpIob = status.pump?.iob;
		if (pumpIob) {
			const iobValue = pumpIob.iob ?? pumpIob.bolusiob;
			if (typeof iobValue === 'number') {
				iobFromDevice = {
					iob: iobValue,
					source: status.connect !== undefined ? 'MM Connect' : 'Pump',
					device: status.device,
					display: `${iobValue.toFixed(2)}U`,
					label: 'IOB',
					info: [],
					level: 'none',
					lastUpdated: status.mills ?? now
				};
				break;
			}
		}
	}

	// Find last bolus from v4 boluses
	const lastBolus = boluses
		.filter(
			(b) => b.insulin && b.insulin > 0 && (b.mills ?? 0) <= now && (b.mills ?? 0) > now - HOURS(24)
		)
		.sort((a, b) => (b.mills ?? 0) - (a.mills ?? 0))[0];

	if (iobFromDevice) {
		if (lastBolus) {
			iobFromDevice.lastBolus = {
				mills: lastBolus.mills ?? 0,
				insulin: lastBolus.insulin ?? 0,
			};
		}
		return iobFromDevice;
	}

	// If no device status, we could calculate IOB from boluses
	// For now, return null if no device data
	return null;
}

/**
 * Process COB data from device status and carb intakes
 */
export function processCOB(
	deviceStatuses: DeviceStatus[],
	carbIntakes: CarbIntake[],
	now: number,
	config: Partial<PillsProcessorConfig> = {}
): COBPillData | null {
	const recencyThreshold = MINUTES(config.cob?.recencyThreshold ?? 30);
	const futureMills = now + MINUTES(5);
	const recentMills = now - recencyThreshold;

	// Try to get COB from device status first (Loop, OpenAPS)
	let cobFromDevice: COBPillData | null = null;

	const recentStatuses = deviceStatuses
		.filter((ds) => {
			const mills = ds.mills ?? (ds.created_at ? new Date(ds.created_at).getTime() : 0);
			return mills <= futureMills && mills >= recentMills;
		})
		.sort((a, b) => {
			const aMills = a.mills ?? (a.created_at ? new Date(a.created_at).getTime() : 0);
			const bMills = b.mills ?? (b.created_at ? new Date(b.created_at).getTime() : 0);
			return bMills - aMills;
		});

	for (const status of recentStatuses) {
		// Check Loop COB
		const loopCob = status.loop?.cob;
		if (loopCob && typeof loopCob.cob === 'number') {
			const mills =
				loopCob.timestamp ? new Date(loopCob.timestamp).getTime() : status.mills ?? now;
			cobFromDevice = {
				cob: loopCob.cob,
				source: 'Loop',
				display: `${Math.round(loopCob.cob * 10) / 10}g`,
				label: 'COB',
				info: [],
				level: 'none',
				lastUpdated: mills
			};
			break;
		}

		// Check OpenAPS COB (from suggested or enacted)
		const openaps = status.openaps;
		if (openaps) {
			const suggested = openaps.suggested as OpenApsSuggestedEnacted | undefined;
			const enacted = openaps.enacted as OpenApsSuggestedEnacted | undefined;

			let lastCOB: number | null = null;
			let lastMoment: number | null = null;

			if (suggested && enacted) {
				const suggestedTime = suggested.timestamp ? new Date(suggested.timestamp).getTime() : 0;
				const enactedTime = enacted.timestamp ? new Date(enacted.timestamp).getTime() : 0;
				if (enactedTime > suggestedTime) {
					lastCOB = enacted.COB ?? null;
					lastMoment = enactedTime;
				} else {
					lastCOB = suggested.COB ?? null;
					lastMoment = suggestedTime;
				}
			} else if (enacted) {
				lastCOB = enacted.COB ?? null;
				lastMoment = enacted.timestamp ? new Date(enacted.timestamp).getTime() : null;
			} else if (suggested) {
				lastCOB = suggested.COB ?? null;
				lastMoment = suggested.timestamp ? new Date(suggested.timestamp).getTime() : null;
			}

			if (lastCOB !== null) {
				cobFromDevice = {
					cob: lastCOB,
					source: 'OpenAPS',
					display: `${Math.round(lastCOB * 10) / 10}g`,
					label: 'COB',
					info: [],
					level: 'none',
					lastUpdated: lastMoment ?? now
				};
				break;
			}
		}
	}

	// Find last carbs from v4 carb intakes
	const lastCarbs = carbIntakes
		.filter(
			(c) => c.carbs && c.carbs > 0 && (c.mills ?? 0) <= now && (c.mills ?? 0) > now - HOURS(24)
		)
		.sort((a, b) => (b.mills ?? 0) - (a.mills ?? 0))[0];

	if (cobFromDevice) {
		if (lastCarbs) {
			cobFromDevice.lastCarbs = {
				mills: lastCarbs.mills ?? 0,
				carbs: lastCarbs.carbs ?? 0,
				food: lastCarbs.foodType,
			};
		}
		return cobFromDevice;
	}

	// If no device data but we have recent carbs, show that
	if (lastCarbs) {
		// Simple COB estimation (not accurate, just for display)
		const carbTime = lastCarbs.mills ?? 0;
		const hoursSinceCarbs = (now - carbTime) / HOURS(1);
		// Assume 30g/hr absorption rate
		const estimatedCOB = Math.max(0, (lastCarbs.carbs ?? 0) - hoursSinceCarbs * 30);

		return {
			cob: estimatedCOB,
			source: 'Care Portal',
			display: `${Math.round(estimatedCOB * 10) / 10}g`,
			label: 'COB',
			info: [],
			level: 'none',
			lastCarbs: {
				mills: lastCarbs.mills ?? 0,
				carbs: lastCarbs.carbs ?? 0,
				food: lastCarbs.foodType,
			},
			isDecaying: estimatedCOB > 0,
			lastUpdated: now
		};
	}

	return null;
}

/**
 * Process CAGE data from device events
 */
export function processCAGE(
	deviceEvents: DeviceEvent[],
	now: number,
	config: Partial<PillsProcessorConfig> = {}
): CAGEPillData | null {
	const infoThreshold = config.cage?.infoThreshold ?? 44;
	const warnThreshold = config.cage?.warnThreshold ?? 48;
	const urgentThreshold = config.cage?.urgentThreshold ?? 72;
	const displayFormat = config.cage?.displayFormat ?? 'hours';

	// Find most recent site change
	const siteChanges = deviceEvents
		.filter(
			(d) =>
				(d.eventType === DeviceEventType.SiteChange || d.eventType === DeviceEventType.CannulaChange) &&
				(d.mills ?? 0) <= now
		)
		.sort((a, b) => (b.mills ?? 0) - (a.mills ?? 0));

	if (siteChanges.length === 0) {
		return null;
	}

	const lastChange = siteChanges[0];
	const treatmentDate = lastChange.mills ?? 0;

	const ageMs = now - treatmentDate;
	const ageHours = Math.floor(ageMs / HOURS(1));
	const ageDays = Math.floor(ageHours / 24);
	const remainingHours = ageHours % 24;

	// Determine alert level
	let level: AlertLevel = 'none';
	if (ageHours >= urgentThreshold) {
		level = 'urgent';
	} else if (ageHours >= warnThreshold) {
		level = 'warn';
	} else if (ageHours >= infoThreshold) {
		level = 'info';
	}

	// Format display
	let display: string;
	if (displayFormat === 'days' && ageHours >= 24) {
		display = `${ageDays}d${remainingHours}h`;
	} else {
		display = `${ageHours}h`;
	}

	return {
		age: ageHours,
		days: ageDays,
		hours: remainingHours,
		timeRemaining: urgentThreshold - ageHours,
		treatmentDate,
		notes: lastChange.notes,
		display,
		label: 'CAGE',
		info: [],
		level,
		lastUpdated: treatmentDate
	};
}

/**
 * Process SAGE data from device events
 */
export function processSAGE(
	deviceEvents: DeviceEvent[],
	now: number,
	config: Partial<PillsProcessorConfig> = {}
): SAGEPillData | null {
	const infoThreshold = config.sage?.infoThreshold ?? 144; // 6 days
	const warnThreshold = config.sage?.warnThreshold ?? 164; // 7 days - 4 hours
	const urgentThreshold = config.sage?.urgentThreshold ?? 166; // 7 days - 2 hours

	// Find most recent sensor change or start
	const sensorEvents = deviceEvents
		.filter(
			(d) =>
				(d.eventType === DeviceEventType.SensorStart || d.eventType === DeviceEventType.SensorChange) &&
				(d.mills ?? 0) <= now
		)
		.sort((a, b) => (b.mills ?? 0) - (a.mills ?? 0));

	if (sensorEvents.length === 0) {
		return null;
	}

	const lastEvent = sensorEvents[0];
	const treatmentDate = lastEvent.mills ?? 0;

	const ageMs = now - treatmentDate;
	const ageHours = Math.floor(ageMs / HOURS(1));
	const ageDays = Math.floor(ageHours / 24);
	const remainingHours = ageHours % 24;

	// Determine alert level
	let level: AlertLevel = 'none';
	if (ageHours >= urgentThreshold) {
		level = 'urgent';
	} else if (ageHours >= warnThreshold) {
		level = 'warn';
	} else if (ageHours >= infoThreshold) {
		level = 'info';
	}

	// Format display
	let display = '';
	if (ageHours >= 24) {
		display = `${ageDays}d${remainingHours}h`;
	} else {
		display = `${ageHours}h`;
	}

	return {
		age: ageHours,
		days: ageDays,
		hours: remainingHours,
		timeRemaining: urgentThreshold - ageHours,
		treatmentDate,
		notes: lastEvent.notes,
		eventType: lastEvent.eventType,
		display,
		label: 'SAGE',
		info: [],
		level,
		lastUpdated: treatmentDate
	};
}

/**
 * Profile time-value entry structure
 */
interface TimeValueEntry {
	time?: string;
	value?: number;
}

/**
 * Get the scheduled basal rate from a profile for a given time
 * @param profile The profile containing basal schedule
 * @param now Current timestamp in milliseconds
 * @returns The scheduled basal rate in U/hr, or 0 if not found
 */
function getScheduledBasalRate(profile: Profile | null, now: number): { rate: number; profileName: string | null } {
	if (!profile) {
		return { rate: 0, profileName: null };
	}

	// Get the active profile name
	const activeProfileName = profile.defaultProfile;
	if (!activeProfileName || !profile.store) {
		return { rate: 0, profileName: null };
	}

	// Get the profile data from the store
	const profileData = profile.store[activeProfileName] as { basal?: TimeValueEntry[] } | undefined;
	if (!profileData?.basal || profileData.basal.length === 0) {
		return { rate: 0, profileName: activeProfileName };
	}

	// Get current time of day
	const date = new Date(now);
	const currentMinutes = date.getHours() * 60 + date.getMinutes();

	// Convert time string (HH:MM) to minutes
	function timeToMinutes(time: string): number {
		const [hours, minutes] = time.split(':').map(Number);
		return hours * 60 + (minutes || 0);
	}

	// Sort basal entries by time
	const sortedBasal = [...profileData.basal]
		.filter((entry): entry is { time: string; value: number } =>
			entry.time !== undefined && entry.value !== undefined
		)
		.sort((a, b) => timeToMinutes(a.time) - timeToMinutes(b.time));

	if (sortedBasal.length === 0) {
		return { rate: 0, profileName: activeProfileName };
	}

	// Find the basal entry that applies at the current time
	// (the last entry with a start time <= current time)
	let currentRate = sortedBasal[sortedBasal.length - 1].value; // Default to last entry (wraps around midnight)

	for (const entry of sortedBasal) {
		const entryMinutes = timeToMinutes(entry.time);
		if (entryMinutes <= currentMinutes) {
			currentRate = entry.value;
		} else {
			break;
		}
	}

	return { rate: currentRate, profileName: activeProfileName };
}

/**
 * Process Basal data from device status and profile
 */
export function processBasal(
	deviceStatuses: DeviceStatus[],
	profile: Profile | null,
	now: number,
	_units: string,
	_config: Partial<PillsProcessorConfig> = {}
): BasalPillData | null {
	if (!profile && deviceStatuses.length === 0) {
		return null;
	}

	// Try to get current basal from recent device status
	const recentStatus = deviceStatuses
		.filter((ds) => {
			const mills = ds.mills ?? (ds.created_at ? new Date(ds.created_at).getTime() : 0);
			return mills <= now && mills > now - MINUTES(30);
		})
		.sort((a, b) => {
			const aMills = a.mills ?? 0;
			const bMills = b.mills ?? 0;
			return bMills - aMills;
		})[0];

	// Extract basal info from device status
	const loopData = recentStatus?.loop;
	const openaps = recentStatus?.openaps;

	// Get scheduled basal from profile
	const { rate: scheduledBasal, profileName } = getScheduledBasalRate(profile, now);

	let totalBasal = scheduledBasal; // Start with scheduled, override if temp basal active
	let isTempBasal = false;
	let isComboActive = false;
	let tempBasalInfo: BasalPillData['tempBasal'] | undefined;

	// Check for temp basal from various sources
	if (openaps?.enacted) {
		const enacted = openaps.enacted as OpenApsSuggestedEnacted;
		if (enacted.rate !== undefined && enacted.duration !== undefined) {
			// Check if the temp basal is still active
			const enactedTimestamp = enacted.timestamp ? new Date(enacted.timestamp).getTime() : now;
			const elapsedMinutes = (now - enactedTimestamp) / 60000;
			const remaining = enacted.duration - elapsedMinutes;

			if (remaining > 0) {
				totalBasal = enacted.rate;
				isTempBasal = true;
				tempBasalInfo = {
					rate: enacted.rate,
					duration: enacted.duration,
					remaining: Math.max(0, remaining),
					startTime: enactedTimestamp
				};
			}
		}
	}

	if (loopData?.enacted) {
		const enacted = loopData.enacted as LoopEnactedData;
		if (enacted.rate !== undefined && enacted.duration !== undefined) {
			// Check if temp basal is still active
			const enactedTimestamp = enacted.timestamp ? new Date(enacted.timestamp).getTime() : now;
			const elapsedMinutes = (now - enactedTimestamp) / 60000;
			const remaining = enacted.duration - elapsedMinutes;

			if (remaining > 0) {
				totalBasal = enacted.rate;
				isTempBasal = true;
				tempBasalInfo = {
					rate: enacted.rate,
					duration: enacted.duration,
					remaining: Math.max(0, remaining),
					startTime: enactedTimestamp
				};
			}
		}
	}

	// If we have no basal data at all, return null
	if (totalBasal === 0 && scheduledBasal === 0 && !profile) {
		return null;
	}

	return {
		totalBasal,
		scheduledBasal,
		isTempBasal,
		isComboActive,
		tempBasal: tempBasalInfo,
		activeProfile: profileName ?? undefined,
		display: `${totalBasal.toFixed(3)}U`,
		label: 'BASAL',
		info: [],
		level: 'none',
		lastUpdated: now
	};
}

/**
 * Process Loop status from device status
 */
export function processLoop(
	deviceStatuses: DeviceStatus[],
	now: number,
	_units: string,
	config: Partial<PillsProcessorConfig> = {}
): LoopPillData | null {
	const warnThreshold = MINUTES(config.loop?.warnThreshold ?? 30);
	const urgentThreshold = MINUTES(config.loop?.urgentThreshold ?? 60);
	const recentMills = now - HOURS(6);

	// Find recent loop/openaps data
	const recentData = deviceStatuses
		.filter((ds) => {
			const mills = ds.mills ?? (ds.created_at ? new Date(ds.created_at).getTime() : 0);
			const hasLoopData = ds.loop !== undefined || ds.openaps !== undefined;
			return hasLoopData && mills <= now && mills >= recentMills;
		})
		.sort((a, b) => {
			const aMills = a.mills ?? 0;
			const bMills = b.mills ?? 0;
			return bMills - aMills;
		});

	if (recentData.length === 0) {
		return null;
	}

	const latestStatus = recentData[0];
	const loopData = latestStatus.loop;
	const openapsData = latestStatus.openaps;

	let result: LoopPillData = {
		status: 'warning',
		symbol: '⚠',
		display: '---',
		label: 'Loop',
		info: [],
		level: 'none'
	};

	// Process Loop data
	if (loopData) {
		const loopTimestamp = loopData.timestamp ? new Date(loopData.timestamp).getTime() : now;
		result.lastLoopTime = loopTimestamp;
		result.loopName = loopData.name ?? 'Loop';

		// Get predicted values - extract eventual BG
		const predictedValues = loopData.predicted?.values;
		if (predictedValues && predictedValues.length > 0) {
			result.eventualBG = predictedValues[predictedValues.length - 1];
		}

		// Get COB/IOB from loop
		if (loopData.cob) {
			result.cob = loopData.cob.cob;
		}
		if (loopData.iob) {
			result.iob = loopData.iob.iob;
		}

		// Get enacted/recommended
		if (loopData.enacted) {
			const enacted = loopData.enacted as LoopEnactedData;
			const enactedTime = enacted.timestamp ? new Date(enacted.timestamp).getTime() : loopTimestamp;

			result.lastEnacted = {
				time: enactedTime,
				type: enacted.bolusVolume ? 'bolus' : enacted.rate === 0 ? 'cancel' : 'temp_basal',
				rate: enacted.rate,
				duration: enacted.duration,
				bolusVolume: enacted.bolusVolume,
				reason: enacted.reason
			};

			// Check if enacted is recent
			if (now - enactedTime < MINUTES(15)) {
				result.status = 'enacted';
				result.symbol = '⌁';
			}

			// Check for failures in enacted data
			if (enacted.failureReason) {
				result.status = 'error';
				result.symbol = 'x';
				result.failureReason = enacted.failureReason;
			}
		}
	}

	// Process OpenAPS data
	if (openapsData) {
		const suggested = openapsData.suggested;
		const enacted = openapsData.enacted;

		if (suggested) {
			const suggestedTime = suggested.timestamp ? new Date(suggested.timestamp).getTime() : now;
			result.lastLoopTime = result.lastLoopTime ?? suggestedTime;
			result.loopName = result.loopName ?? 'OpenAPS';

			// Get IOB/COB
			if (suggested.COB !== undefined) {
				result.cob = suggested.COB;
			}

			// Get eventual BG
			if (suggested.eventualBG !== undefined) {
				result.eventualBG = suggested.eventualBG;
			}
		}

		if (enacted) {
			const enactedTime = enacted.timestamp ? new Date(enacted.timestamp).getTime() : now;

			result.lastEnacted = {
				time: enactedTime,
				type: enacted.rate === 0 ? 'cancel' : 'temp_basal',
				rate: enacted.rate,
				duration: enacted.duration,
				reason: enacted.reason
			};

			// Check if enacted is recent and successful
			if (enacted.received && now - enactedTime < MINUTES(15)) {
				result.status = 'enacted';
				result.symbol = '⌁';
			}
		}

		// Get IOB from OpenAPS
		const iobData = openapsData.iob;
		if (iobData) {
			const iob = Array.isArray(iobData) ? iobData[0] : iobData;
			if (iob?.iob !== undefined) {
				result.iob = iob.iob;
			}
		}
	}

	// Determine last OK moment and status
	if (result.lastLoopTime) {
		result.lastOkTime = result.lastLoopTime;

		const timeSinceLoop = now - result.lastLoopTime;

		// Determine level based on time since last loop
		if (timeSinceLoop >= urgentThreshold) {
			result.level = 'urgent';
			if (result.status !== 'error') {
				result.status = 'warning';
				result.symbol = '⚠';
			}
		} else if (timeSinceLoop >= warnThreshold) {
			result.level = 'warn';
			if (result.status !== 'error' && result.status !== 'enacted') {
				result.status = 'warning';
				result.symbol = '⚠';
			}
		} else {
			// Recent loop
			if (result.status !== 'enacted' && result.status !== 'error') {
				result.status = 'looping';
				result.symbol = '↻';
			}
		}
	}

	return result;
}
