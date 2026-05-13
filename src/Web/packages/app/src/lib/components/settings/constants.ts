import type { ClientSettings } from '$lib/stores/serverSettings.js';

export function getDefaultSettings(): ClientSettings {
	return {
		units: 'mg/dl',
		timeFormat: 12,
		nightMode: false,
		showBGON: true,
		showIOB: true,
		showCOB: true,
		showBasal: true,
		showPlugins: ['delta', 'direction', 'timeago', 'devicestatus'],
		language: 'en',
		theme: 'default',
		alarmUrgentHigh: true,
		alarmUrgentHighMins: [15, 30, 60],
		alarmHigh: true,
		alarmHighMins: [30, 60],
		alarmLow: true,
		alarmLowMins: [15, 30, 45, 60],
		alarmUrgentLow: true,
		alarmUrgentLowMins: [5, 10, 15, 30],
		alarmTimeagoWarn: true,
		alarmTimeagoWarnMins: 15,
		alarmTimeagoUrgent: true,
		alarmTimeagoUrgentMins: 30,
		showForecast: true,
		focusHours: 12,
		heartbeat: 60,
		baseURL: '',
		authDefaultRoles: 'readable',
		thresholds: {
			high: 260,
			targetTop: 180,
			targetBottom: 80,
			low: 55
		},
		demoMode: {
			enabled: false,
			realTimeUpdates: false,
			webSocketUrl: '',
			showDemoIndicators: false
		},
		titleFavicon: {
			enabled: true,
			showBgValue: true,
			showDirection: true,
			showDelta: false,
			customPrefix: 'Nocturne',
			faviconEnabled: true,
			faviconShowBg: true,
			faviconColorCoded: true,
			flashOnAlarm: true
		}
	};
}
