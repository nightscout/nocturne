import type { LegendState } from "./context.svelte.js";

/**
 * The legend's toggles, as the dashboard starts them: override, profile and activity lanes
 * hidden, everything else shown. Hiding pump modes also collapses their list.
 */
export function createLegendState(): LegendState {
	let showIob = $state(true);
	let showCob = $state(true);
	let showBasal = $state(true);
	let showBolus = $state(true);
	let showCarbs = $state(true);
	let showDeviceEvents = $state(true);
	let showAlarms = $state(true);
	let showScheduledTrackers = $state(true);
	let showOverrideSpans = $state(false);
	let showProfileSpans = $state(false);
	let showActivitySpans = $state(false);
	let showBasalInjections = $state(true);
	let showPumpModes = $state(true);
	let expandedPumpModes = $state(false);

	return {
		get iob() { return showIob; },
		get cob() { return showCob; },
		get basal() { return showBasal; },
		get bolus() { return showBolus; },
		get carbs() { return showCarbs; },
		get deviceEvents() { return showDeviceEvents; },
		get alarms() { return showAlarms; },
		get scheduledTrackers() { return showScheduledTrackers; },
		get basalInjections() { return showBasalInjections; },
		get overrideSpans() { return showOverrideSpans; },
		get profileSpans() { return showProfileSpans; },
		get activitySpans() { return showActivitySpans; },
		get pumpModes() { return showPumpModes; },
		get expandedPumpModes() { return expandedPumpModes; },
		toggle(key: string) {
			switch (key) {
				case "iob": showIob = !showIob; break;
				case "cob": showCob = !showCob; break;
				case "basal": showBasal = !showBasal; break;
				case "bolus": showBolus = !showBolus; break;
				case "carbs": showCarbs = !showCarbs; break;
				case "deviceEvents": showDeviceEvents = !showDeviceEvents; break;
				case "alarms": showAlarms = !showAlarms; break;
				case "scheduledTrackers": showScheduledTrackers = !showScheduledTrackers; break;
				case "basalInjections": showBasalInjections = !showBasalInjections; break;
				case "overrideSpans": showOverrideSpans = !showOverrideSpans; break;
				case "profileSpans": showProfileSpans = !showProfileSpans; break;
				case "activitySpans": showActivitySpans = !showActivitySpans; break;
				case "expandedPumpModes": expandedPumpModes = !expandedPumpModes; break;
				case "pumpModes":
					showPumpModes = !showPumpModes;
					if (!showPumpModes) expandedPumpModes = false;
					break;
			}
		},
	};
}
