import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import RuleBuilderLeafEditor from "./RuleBuilderLeafEditor.svelte";
import { AlertLimitFact, buildBody, defaultPayload, ForecastCurve, parseRule, SensorState, type ConditionNode } from "./types";
import { flushSync } from "svelte";
import { AlertConditionType } from "@nocturne/api-client";
import { glucoseUnits } from "#lib/stores/appearance-store.svelte.js";

describe("RuleBuilderLeafEditor is_active", () => {
	it("shows a stored leaf missing is_active as off, as the engines read it", async () => {
		const state = parseRule({
			name: "Stored",
			conditionType: AlertConditionType.PumpSuspended,
			conditionParams: { for_minutes: 30 },
		});
		const leaf = state.condition!.composite!.conditions[0];

		render(RuleBuilderLeafEditor, { props: { node: leaf } });

		await expect.element(page.getByRole("switch")).not.toBeChecked();
		await expect.element(page.getByText("inactive")).toBeVisible();
	});

	it("shows a new leaf as on, as it is stored", async () => {
		const leaf = defaultPayload("pump_state");

		render(RuleBuilderLeafEditor, { props: { node: leaf } });

		await expect.element(page.getByRole("switch")).toBeChecked();
		await expect.element(page.getByText("active", { exact: true })).toBeVisible();
		expect(leaf.pump_state?.is_active).toBe(true);
	});
});

describe("saving is_active", () => {
	it("saves false for a stored leaf missing it", () => {
		const state = parseRule({
			name: "Stored",
			conditionType: AlertConditionType.StateSpanActive,
			conditionParams: { category: "Override" },
		});
		expect(buildBody(state).conditionParams).toEqual({ category: "Override", is_active: false });
	});

	it("saves true for a new leaf", () => {
		const state = parseRule(null);
		state.condition!.composite!.conditions = [defaultPayload("sleep_session_active")];
		expect(buildBody(state).conditionParams).toEqual({ is_active: true });
	});
});

describe("RuleBuilderLeafEditor clear margin", () => {
	it("shows a set margin in the display unit and stores what is typed in mg/dL", async () => {
		glucoseUnits.current = "mmol";
		try {
			const leaf: ConditionNode = {
				type: "threshold",
				threshold: { direction: "below", value: 72, clear_margin: 18 },
			};
			render(RuleBuilderLeafEditor, { props: { node: leaf } });

			const input = page.getByRole("spinbutton", { name: "Clear margin" });
			await expect.element(input).toHaveValue(1);
			await input.fill("0.5");
			expect(leaf.threshold?.clear_margin).toBeCloseTo(9, 0);
		} finally {
			glucoseUnits.current = "mg/dl";
		}
	});

	it("shows no margin input for a leaf without one", async () => {
		render(RuleBuilderLeafEditor, { props: { node: defaultPayload("iob") } });

		await expect.element(page.getByRole("spinbutton", { name: "Value" })).toBeVisible();
		await expect.element(page.getByRole("spinbutton", { name: "Clear margin" })).not.toBeInTheDocument();
	});

	it("hides a margin in auto-resolve conditions, which read none", async () => {
		const leaf: ConditionNode = {
			type: "iob",
			iob: { operator: ">", value: 2, clear_margin: 1 },
		};
		render(RuleBuilderLeafEditor, { props: { node: leaf, purpose: "resolve" } });

		await expect.element(page.getByRole("spinbutton", { name: "Value" })).toBeVisible();
		await expect.element(page.getByRole("spinbutton", { name: "Clear margin" })).not.toBeInTheDocument();
	});

	it("hides a margin on an exact-value comparison, which cannot have one", async () => {
		const leaf: ConditionNode = {
			type: "iob",
			iob: { operator: "==", value: 2, clear_margin: 1 },
		};
		render(RuleBuilderLeafEditor, { props: { node: leaf } });

		await expect.element(page.getByRole("spinbutton", { name: "Value" })).toBeVisible();
		await expect.element(page.getByRole("spinbutton", { name: "Clear margin" })).not.toBeInTheDocument();
	});

	it("does not let a typed negative margin through", async () => {
		const leaf: ConditionNode = {
			type: "cob",
			cob: { operator: "<", value: 10, clear_margin: 2 },
		};
		render(RuleBuilderLeafEditor, { props: { node: leaf } });

		await page.getByRole("spinbutton", { name: "Clear margin" }).fill("-3");
		expect(leaf.cob?.clear_margin).toBe(0);
	});

	it("steps a value and its clear margin alike, in each unit", async () => {
		const cases: [ConditionNode, string, string, string][] = [
			[
				{ type: "threshold", threshold: { direction: "below", value: 70, clear_margin: 5 } },
				"Value",
				"1",
				"0.1",
			],
			[
				{ type: "rate_of_change", rate_of_change: { direction: "falling", rate: 2, clear_margin: 1 } },
				"Value",
				"0.1",
				"0.01",
			],
			[{ type: "cob", cob: { operator: "<", value: 10, clear_margin: 2 } }, "Value", "0.1", "0.1"],
			[
				{ type: "carbs_required", carbs_required: { operator: ">", value: 10, clear_margin: 2 } },
				"Value",
				"1",
				"1",
			],
			[
				{ type: "sensitivity_ratio", sensitivity_ratio: { operator: "<", value: 0.9, clear_margin: 0.1 } },
				"Value",
				"0.01",
				"0.01",
			],
		];
		for (const units of ["mg/dl", "mmol"] as const) {
			glucoseUnits.current = units;
			try {
				for (const [node, label, mgdlStep, mmolStep] of cases) {
					const { unmount } = render(RuleBuilderLeafEditor, { props: { node } });
					const expected = units === "mmol" ? mmolStep : mgdlStep;
					await expect.element(page.getByRole("spinbutton", { name: label })).toHaveAttribute("step", expected);
					await expect
						.element(page.getByRole("spinbutton", { name: "Clear margin" }))
						.toHaveAttribute("step", expected);
					unmount();
				}
			} finally {
				glucoseUnits.current = "mg/dl";
			}
		}
	});

	it("keeps the fraction of a rate margin and rate in mg/dL", async () => {
		glucoseUnits.current = "mg/dl";
		const leaf: ConditionNode = $state({
			type: "rate_of_change",
			rate_of_change: { direction: "falling", rate: 2, clear_margin: 1 },
		});
		render(RuleBuilderLeafEditor, { props: { node: leaf } });

		await page.getByRole("spinbutton", { name: "Clear margin" }).fill("0.4");
		expect(leaf.rate_of_change?.clear_margin).toBe(0.4);
		await expect.element(page.getByRole("spinbutton", { name: "Clear margin" })).toHaveValue(0.4);
		await page.getByRole("spinbutton", { name: "Value" }).fill("1.5");
		expect(leaf.rate_of_change?.rate).toBe(1.5);
		await expect.element(page.getByRole("spinbutton", { name: "Value" })).toHaveValue(1.5);
	});

	it("keeps the fraction of a rate margin and rate in mmol/L", async () => {
		glucoseUnits.current = "mmol";
		try {
			const leaf: ConditionNode = $state({
				type: "rate_of_change",
				rate_of_change: { direction: "rising", rate: 2, clear_margin: 1 },
			});
			render(RuleBuilderLeafEditor, { props: { node: leaf } });

			await page.getByRole("spinbutton", { name: "Clear margin" }).fill("0.05");
			expect(leaf.rate_of_change?.clear_margin).toBe(0.9);
			await expect.element(page.getByRole("spinbutton", { name: "Clear margin" })).toHaveValue(0.05);
			await page.getByRole("spinbutton", { name: "Value" }).fill("0.01");
			expect(leaf.rate_of_change?.rate).toBe(0.18);
			await expect.element(page.getByRole("spinbutton", { name: "Value" })).toHaveValue(0.01);
		} finally {
			glucoseUnits.current = "mg/dl";
		}
	});

	it("says a stored margin inside “for at least” cannot run, and removes it", async () => {
		const leaf: ConditionNode = $state({
			type: "threshold",
			threshold: { direction: "below", value: 70, clear_margin: 5 },
		});
		render(RuleBuilderLeafEditor, { props: { node: leaf, underSustained: true } });

		await expect.element(page.getByRole("spinbutton", { name: "Clear margin" })).not.toBeInTheDocument();
		await expect
			.element(page.getByText("A condition inside “for at least” cannot have a clear margin."))
			.toBeVisible();
		await page.getByRole("button", { name: "Remove the clear margin" }).click();
		expect(leaf.threshold?.clear_margin).toBeUndefined();
		await expect
			.element(page.getByText("A condition inside “for at least” cannot have a clear margin."))
			.not.toBeInTheDocument();
	});
});

describe("RuleBuilderLeafEditor sensor state minutes", () => {
	it("offers minutes only when No value is the only state, and says why otherwise", async () => {
		const leaf: ConditionNode = { type: "sensor_state", sensor_state: { states: [SensorState.Error] } };

		render(RuleBuilderLeafEditor, { props: { node: leaf } });

		await expect.element(page.getByRole("spinbutton", { name: "Minutes, optional" })).toBeDisabled();
		await expect.element(page.getByText("Only for No value. The other states have no start time.")).toBeVisible();
	});

	it("lets No value alone take minutes", async () => {
		const leaf: ConditionNode = {
			type: "sensor_state",
			sensor_state: { states: [SensorState.NoValue], for_minutes: 30 },
		};

		render(RuleBuilderLeafEditor, { props: { node: leaf } });

		await expect.element(page.getByRole("spinbutton", { name: "Minutes, optional" })).toBeEnabled();
		await expect.element(page.getByRole("spinbutton", { name: "Minutes, optional" })).toHaveValue(30);
		await expect.element(page.getByText("Only for No value. The other states have no start time.")).not.toBeInTheDocument();
	});

	it("drops the minutes when another state joins No value", async () => {
		const leaf: ConditionNode = {
			type: "sensor_state",
			sensor_state: { states: [SensorState.NoValue], for_minutes: 30 },
		};

		render(RuleBuilderLeafEditor, { props: { node: leaf } });
		await page.getByRole("button", { name: "Error" }).click();

		expect(leaf.sensor_state?.states).toEqual([SensorState.NoValue, SensorState.Error]);
		expect(leaf.sensor_state?.for_minutes).toBeUndefined();
	});
});

describe("RuleBuilderLeafEditor limit from a fact", () => {
	it("compares with a target in place of a number, and stores no number", async () => {
		const leaf = $state<ConditionNode>({ type: "threshold", threshold: { direction: "below", value: 70 } });
		render(RuleBuilderLeafEditor, { props: { node: leaf } });

		await page.getByLabelText("Compare with").click();
		await page.getByRole("option", { name: "Target low" }).click();
		flushSync();

		expect(leaf.threshold?.value_from).toEqual({ fact: AlertLimitFact.TargetLow, offset: undefined });
		expect(leaf.threshold?.value).toBeUndefined();
		await expect.element(page.getByRole("spinbutton", { name: "Offset" })).toBeVisible();
		await expect
			.element(page.getByText("If your profile has no target range, this condition does not know if it is true."))
			.toBeVisible();
	});

	it("stores a signed offset typed in mmol/L in mg/dL", async () => {
		glucoseUnits.current = "mmol";
		const leaf = $state<ConditionNode>({
			type: "eventual_glucose",
			eventual_glucose: { operator: "<", value_from: { fact: AlertLimitFact.TargetLow } },
		});
		render(RuleBuilderLeafEditor, { props: { node: leaf } });

		await page.getByRole("spinbutton", { name: "Offset" }).fill("-1");

		expect(leaf.eventual_glucose?.value_from?.offset).toBeCloseTo(-18, 0);
		glucoseUnits.current = "mg/dl";
	});

	it("offers the latest glucose to a forecast but not to a threshold", async () => {
		render(RuleBuilderLeafEditor, {
			props: { node: { type: "threshold", threshold: { direction: "below", value: 70 } } },
		});
		await page.getByLabelText("Compare with").click();

		await expect.element(page.getByRole("option", { name: "Target high" })).toBeVisible();
		expect(page.getByRole("option", { name: "Latest glucose" }).elements()).toHaveLength(0);
	});

	it("goes back to a number and drops the fact", async () => {
		const leaf = $state<ConditionNode>({
			type: "predicted",
			predicted: { operator: "<", within_minutes: 30, value_from: { fact: AlertLimitFact.LatestGlucose, offset: -30 } },
		});
		render(RuleBuilderLeafEditor, { props: { node: leaf } });

		await page.getByLabelText("Compare with").click();
		await page.getByRole("option", { name: "Number" }).click();
		flushSync();

		expect(leaf.predicted?.value_from).toBeUndefined();
		expect(leaf.predicted?.value).toBe(70);
	});
});

describe("RuleBuilderLeafEditor forecast curve", () => {
	const forecast = (choice: object = {}): ConditionNode => ({
		type: "predicted",
		predicted: { operator: "<", value: 70, within_minutes: 30, ...choice },
	});

	it("shows no curve choice for a condition that reads the default forecast", async () => {
		render(RuleBuilderLeafEditor, { props: { node: forecast() } });

		await expect.element(page.getByLabelText("Forecast choice")).not.toBeInTheDocument();
	});

	it("shows the one curve a condition reads, and says what an absent curve does", async () => {
		render(RuleBuilderLeafEditor, { props: { node: forecast({ curve: ForecastCurve.Cob }) } });

		await expect.element(page.getByLabelText("Forecast curve")).toHaveTextContent("COB forecast");
		await expect
			.element(page.getByText("If the loop does not send a curve that you select, the rule waits and shows the cause."))
			.toBeVisible();
	});

	it("keeps at least one curve in a lowest-of list", async () => {
		const leaf = forecast({ min_of: [ForecastCurve.Iob, ForecastCurve.Zt] });
		render(RuleBuilderLeafEditor, { props: { node: leaf } });

		await page.getByRole("button", { name: "UAM forecast" }).click();
		expect(leaf.predicted?.min_of).toEqual([ForecastCurve.Iob, ForecastCurve.Zt, ForecastCurve.Uam]);

		await page.getByRole("button", { name: "IOB forecast" }).click();
		await page.getByRole("button", { name: "Zero temp forecast" }).click();
		await page.getByRole("button", { name: "UAM forecast" }).click();
		expect(leaf.predicted?.min_of).toEqual([ForecastCurve.Uam]);
	});

	it("edits the eventual glucose in the display unit and stores mg/dL", async () => {
		glucoseUnits.current = "mmol";
		const leaf: ConditionNode = { type: "eventual_glucose", eventual_glucose: { operator: "<", value: 90 } };
		render(RuleBuilderLeafEditor, { props: { node: leaf } });

		await page.getByRole("spinbutton", { name: "Value" }).fill("4");

		expect(leaf.eventual_glucose?.value).toBeCloseTo(72, 0);
		glucoseUnits.current = "mg/dl";
	});
});
