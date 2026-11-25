import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ALERT_PRESETS } from "#lib/test-stubs/alert-presets.js";
import { glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
import { convertRateFromDisplayUnits } from "#lib/utils/formatting.js";

vi.mock("#lib/api/generated/alertRules.generated.remote.js", () => ({
  getSettingOptions: () => ({ current: undefined, error: undefined }),
}));

import { parseRule } from "../types";
import SettingsStep from "./SettingsStep.svelte";
import { newDraft, templatesFrom, type TemplateId } from "./templates.svelte";

const TEMPLATES = templatesFrom(ALERT_PRESETS);

function draftFor(id: TemplateId) {
  return newDraft(TEMPLATES.find((t) => t.id === id)!);
}

function renderStep(draft: ReturnType<typeof draftFor>) {
  render(SettingsStep, {
    props: { draft, editor: parseRule(null), availableRules: [] },
  });
}

const rateInput = () =>
  page.getByRole("spinbutton", {
    name: "Alert when glucose decreases this fast, or faster",
  });

describe("SettingsStep rate input", () => {
  afterEach(() => {
    glucoseUnits.current = "mg/dl";
  });

  it("shows a rate of 1.5 mg/dL per minute as 1.5", async () => {
    const draft = draftFor("falling_fast");
    draft.leaf!.rate_of_change!.rate = 1.5;
    renderStep(draft);

    await expect.element(rateInput()).toHaveValue(1.5);
    await expect.element(rateInput()).toHaveAttribute("step", "0.1");
  });

  it("shows the same rate as its fraction in mmol/L per minute", async () => {
    glucoseUnits.current = "mmol";
    const draft = draftFor("falling_fast");
    draft.leaf!.rate_of_change!.rate = 1.5;
    renderStep(draft);

    await expect.element(rateInput()).toHaveValue(0.08);
    await expect.element(rateInput()).toHaveAttribute("step", "0.01");
  });

  it("stores a typed 0.08 mmol/L per minute without losing the fraction", async () => {
    glucoseUnits.current = "mmol";
    const draft = draftFor("falling_fast");
    renderStep(draft);

    await rateInput().fill("0.08");

    const stored = draft.leaf!.rate_of_change!.rate;
    expect(stored).toBe(convertRateFromDisplayUnits(0.08, "mmol"));
    expect(stored).toBeCloseTo(1.44, 2);
  });
});

describe("SettingsStep glucose input", () => {
  afterEach(() => {
    glucoseUnits.current = "mg/dl";
  });

  it("keeps converting a threshold as glucose", async () => {
    glucoseUnits.current = "mmol";
    const draft = draftFor("low");
    draft.leaf!.threshold!.value = 72;
    renderStep(draft);

    const input = page.getByRole("spinbutton", {
      name: "Alert when glucose is below",
    });
    await expect.element(input).toHaveValue(4);
    await expect.element(input).toHaveAttribute("step", "0.1");
  });
});
