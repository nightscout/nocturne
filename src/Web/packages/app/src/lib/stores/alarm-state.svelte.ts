// Alarm state management using Svelte 5 runes
// Connects SignalR alarm events to audio playback and the existing AlarmActiveView overlay
//
// This state is module-level, which on the server means one copy shared by every
// concurrent request. That is only safe because nothing writes it there: alarms
// arrive over SignalR, which is browser-only, and every mutating entry point
// below bails out when `browser` is false. Keep that guard on anything new — a
// server-side write here would show one tenant's alarm to whoever else was
// mid-render.
import { browser } from '$app/environment';
import { playAlarmSound, stopAlarmSound } from '$lib/audio/alarm-sounds';
import type { AlarmEvent } from '$lib/websocket/types';
import type { AlarmProfileConfiguration } from '$lib/types/alarm-profile';
import { createDefaultAlarmProfile } from '$lib/types/alarm-profile';

export interface ActiveAlarm {
	event: AlarmEvent;
	profile: AlarmProfileConfiguration;
	triggeredAt: number;
}

let activeAlarm = $state<ActiveAlarm | null>(null);
let isSnoozed = $state(false);
let snoozeUntil = $state<number | null>(null);
let snoozeTimer = $state<ReturnType<typeof setTimeout> | null>(null);
let isFlashing = $state(false);
let flashInterval = $state<ReturnType<typeof setInterval> | null>(null);

/**
 * Resolve an AlarmEvent level to the best matching user alarm profile.
 */
function resolveProfile(
	event: AlarmEvent,
	profiles: AlarmProfileConfiguration[]
): AlarmProfileConfiguration {
	const levelMap: Record<string, string[]> = {
		urgent: ['UrgentLow', 'UrgentHigh'],
		warn: ['Low', 'High'],
		warning: ['Low', 'High'],
	};

	const candidates = levelMap[event.level] ?? ['Low'];
	const match = profiles.find((p) => p.enabled && candidates.includes(p.alarmType));
	if (match) return match;

	// Fall back to a sensible default profile
	const isUrgent = event.level === 'urgent';
	return createDefaultAlarmProfile(isUrgent ? 'UrgentLow' : 'Low');
}

/**
 * Trigger an alarm from a SignalR alarm event.
 * Pass the user's alarm profiles so we resolve the right sound/visual settings.
 */
export function trigger(event: AlarmEvent, profiles: AlarmProfileConfiguration[] = []) {
	if (!browser) return;
	if (isSnoozed && snoozeUntil && Date.now() < snoozeUntil) {
		return;
	}

	isSnoozed = false;
	snoozeUntil = null;

	const profile = resolveProfile(event, profiles);
	activeAlarm = { event, profile, triggeredAt: Date.now() };

	if (profile.audio.enabled) {
		playAlarmSound(profile.audio.soundId, {
			volume: profile.audio.maxVolume,
			ascending: profile.audio.ascendingVolume,
			startVolume: profile.audio.startVolume,
			ascendDurationSeconds: profile.audio.ascendDurationSeconds,
			vibrate: profile.vibration.enabled,
		});
	}

	startFlashing(profile);
}

export function dismiss() {
	if (!browser) return;
	const defaultMinutes = activeAlarm?.profile.snooze.defaultMinutes ?? 15;
	stopAlarmSound();
	stopFlashing();
	activeAlarm = null;

	// Apply the default snooze period so continuous alarm events
	// from the server don't immediately re-trigger the alarm.
	isSnoozed = true;
	snoozeUntil = Date.now() + defaultMinutes * 60 * 1000;

	if (snoozeTimer) clearTimeout(snoozeTimer);
	snoozeTimer = setTimeout(() => {
		isSnoozed = false;
		snoozeUntil = null;
		snoozeTimer = null;
	}, defaultMinutes * 60 * 1000);
}

export function snooze(minutes: number) {
	if (!browser) return;
	stopAlarmSound();
	stopFlashing();
	activeAlarm = null;
	isSnoozed = true;
	snoozeUntil = Date.now() + minutes * 60 * 1000;

	if (snoozeTimer) clearTimeout(snoozeTimer);
	snoozeTimer = setTimeout(() => {
		isSnoozed = false;
		snoozeUntil = null;
		snoozeTimer = null;
	}, minutes * 60 * 1000);
}

export function clear() {
	if (!browser) return;
	stopAlarmSound();
	stopFlashing();
	activeAlarm = null;
	isSnoozed = false;
	snoozeUntil = null;
	if (snoozeTimer) {
		clearTimeout(snoozeTimer);
		snoozeTimer = null;
	}
}

function startFlashing(profile: AlarmProfileConfiguration) {
	stopFlashing();
	if (profile.visual.screenFlash) {
		flashInterval = setInterval(() => {
			isFlashing = !isFlashing;
		}, profile.visual.flashIntervalMs);
	}
}

function stopFlashing() {
	if (flashInterval) {
		clearInterval(flashInterval);
		flashInterval = null;
	}
	isFlashing = false;
}

export function getActiveAlarm(): ActiveAlarm | null {
	return activeAlarm;
}

export function getIsSnoozed(): boolean {
	return isSnoozed;
}

export function getIsFlashing(): boolean {
	return isFlashing;
}
