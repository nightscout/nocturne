import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { afterEach, describe, expect, it } from "vitest";
import { timeFormat } from "#lib/stores/appearance-store.svelte.js";
import type { BasalPillData } from "#lib/types/status-pills.js";
import BasalPill from "./BasalPill.svelte";

const doseAt = new Date(2026, 0, 15, 14, 30).getTime();

const pump: BasalPillData = {
  totalBasal: 0.85,
  scheduledBasal: 0.85,
  isTempBasal: false,
  isComboActive: false,
  display: "0.850U/h",
  label: "BASAL",
  info: [],
  level: "none",
};

const injection = (estimatedRate?: number, insulinName?: string): BasalPillData => ({
  totalBasal: estimatedRate ?? 0,
  scheduledBasal: 0,
  isTempBasal: false,
  isComboActive: false,
  injection: { units: 18, mills: doseAt, insulinName, estimatedRate },
  display: "18 U",
  label: "BASAL",
  info: [],
  level: "none",
});

describe("BasalPill on injections", () => {
  afterEach(() => timeFormat.hydrate("12"));

  it("shows the last dose and how long ago it was given", async () => {
    render(BasalPill, {
      data: { ...injection(0.75, "Tresiba"), injection: { units: 18, mills: Date.now() - 9 * 3_600_000 } },
    });

    await expect.element(page.getByText("18 U · 9h ago")).toBeVisible();
  });

  it("shows the insulin, the dose time and the estimated rate in the popover", async () => {
    timeFormat.hydrate("24");
    render(BasalPill, { data: injection(0.75, "Tresiba") });

    await page.getByRole("button", { name: /Basal/ }).click();
    await expect.element(page.getByText("Tresiba")).toBeVisible();
    await expect.element(page.getByText("14:30")).toBeVisible();
    await expect.element(page.getByText("Estimated rate")).toBeVisible();
    await expect.element(page.getByText("0.75 U/h")).toBeVisible();
  });

  it("follows the 12-hour preference for the dose time", async () => {
    timeFormat.hydrate("12");
    render(BasalPill, { data: injection(0.75, "Tresiba") });

    await page.getByRole("button", { name: /Basal/ }).click();
    await expect.element(page.getByText(/2:30\s?pm/i)).toBeVisible();
  });

  it("omits the estimated rate once no dose covers now", async () => {
    const { container } = render(BasalPill, { data: injection(undefined, "Tresiba") });

    await page.getByRole("button", { name: /Basal/ }).click();
    await expect.element(page.getByText("Tresiba")).toBeVisible();
    expect(container.textContent).not.toContain("Estimated rate");
  });
});

describe("BasalPill dose time", () => {
  afterEach(() => timeFormat.hydrate("12"));

  it("shows the time alone for a dose given today", async () => {
    timeFormat.hydrate("24");
    const today = new Date();
    today.setHours(0, 5, 0, 0);
    render(BasalPill, {
      data: { ...injection(0.75), injection: { units: 18, mills: today.getTime() } },
    });

    await page.getByRole("button", { name: /Basal/ }).click();
    await expect.element(page.getByText("00:05", { exact: true })).toBeVisible();
  });

  it("adds the date for a dose given on an earlier day, in the time preference", async () => {
    timeFormat.hydrate("24");
    render(BasalPill, { data: injection(0.75) });

    await page.getByRole("button", { name: /Basal/ }).click();
    await expect.element(page.getByText(/Jan.*14:30/)).toBeVisible();
  });
});

describe("BasalPill on a pump", () => {
  it("labels the profile rate in the popover per hour", async () => {
    render(BasalPill, {
      data: {
        ...pump,
        isTempBasal: true,
        tempBasal: { rate: 1.2, duration: 30, remaining: 20, startTime: doseAt },
      },
    });

    await page.getByRole("button", { name: /Temp basal/ }).click();
    await expect.element(page.getByText("0.850 U/h")).toBeVisible();
  });

  it("shows the rate per hour and no dose rows", async () => {
    const { container } = render(BasalPill, { data: pump });

    await expect.element(page.getByText("0.850U/h")).toBeVisible();
    expect(container.textContent).not.toContain("·");
  });
});
