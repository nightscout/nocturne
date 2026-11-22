import type { AlertRuleSettingOptionsResponse } from "@nocturne/api-client";

/** What the alert rules' setting-options read answers, for a test that renders the rule editor. */
export const ALERT_SETTING_OPTIONS: AlertRuleSettingOptionsResponse = {
	onUnknownValues: ["hold", "met", "not_met"],
	defaultOnUnknown: "hold",
	defaultGlucoseFreshnessMinutes: 11,
	minGlucoseFreshnessMinutes: 1,
	maxGlucoseFreshnessMinutes: 1440,
};
