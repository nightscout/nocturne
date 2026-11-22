import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import RuleBuilderLeafEditor from "./RuleBuilderLeafEditor.svelte";
import { buildBody, defaultPayload, parseRule, type ConditionNode } from "./types";
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
