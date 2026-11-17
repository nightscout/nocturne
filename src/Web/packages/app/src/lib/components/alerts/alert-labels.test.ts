import { describe, it, expect } from "vitest";
import { GlucoseBucket, PumpModeState, StateSpanCategory, TempBasalMetric, TREND_BUCKETS } from "./types";
import {
	ALERT_STATES,
	alertStateLabel,
	glucoseBucketLabel,
	pumpModeLabel,
	rateDirectionLabel,
	stateSpanCategoryLabel,
	tempBasalMetricLabel,
	trendBucketLabel,
} from "./alert-labels.svelte";

function expectStandaloneLabels(values: string[], labelFor: (value: never) => string) {
	const labels = values.map((value) => labelFor(value as never));
	for (const [index, label] of labels.entries()) {
		if (/^\p{Ll}/u.test(values[index])) expect(label).not.toBe(values[index]);
	}
	for (const label of labels) {
		expect(label).toMatch(/^\p{Lu}/u);
		expect(label).not.toMatch(/^([A-Z]|\P{L})+$/u);
	}
	expect(new Set(labels).size).toBe(labels.length);
}

describe("alert labels", () => {
	it("labels every glucose bucket", () => {
		expectStandaloneLabels(Object.values(GlucoseBucket), glucoseBucketLabel);
	});

	it("labels every pump mode, naming Liberty as a mode", () => {
		expectStandaloneLabels(Object.values(PumpModeState), pumpModeLabel);
		expect(pumpModeLabel(PumpModeState.EaseOff)).toBe("Ease off");
		expect(pumpModeLabel(PumpModeState.Liberty)).toBe("Liberty");
	});

	it("labels every state-span category", () => {
		expectStandaloneLabels(Object.values(StateSpanCategory), stateSpanCategoryLabel);
		expect(stateSpanCategoryLabel(StateSpanCategory.Motion)).toBe("Motion");
	});

	it("labels every alert state", () => {
		expectStandaloneLabels([...ALERT_STATES], alertStateLabel);
	});

	it("labels every trend bucket", () => {
		expectStandaloneLabels([...TREND_BUCKETS], trendBucketLabel);
		expect(TREND_BUCKETS.map(trendBucketLabel)).toEqual([
			"Falling fast",
			"Falling",
			"Flat",
			"Rising",
			"Rising fast",
		]);
	});

	it("labels every rate direction", () => {
		expectStandaloneLabels(["falling", "rising"], rateDirectionLabel);
	});

	it("labels every temp basal metric", () => {
		expectStandaloneLabels(Object.values(TempBasalMetric), tempBasalMetricLabel);
		expect(tempBasalMetricLabel(TempBasalMetric.PercentOfScheduled)).toBe("Percent of scheduled");
	});

	it("returns a value the enum does not hold as it came", () => {
		expect(glucoseBucketLabel("odd" as never)).toBe("odd");
		expect(alertStateLabel("odd" as never)).toBe("odd");
		expect(pumpModeLabel("odd" as never)).toBe("odd");
		expect(stateSpanCategoryLabel("odd" as never)).toBe("odd");
		expect(trendBucketLabel("odd" as never)).toBe("odd");
		expect(rateDirectionLabel("odd" as never)).toBe("odd");
		expect(tempBasalMetricLabel("odd" as never)).toBe("odd");
	});
});
