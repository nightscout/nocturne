import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { afterEach, describe, it, expect } from "vitest";
import { timeFormat } from "#lib/stores/appearance-store.svelte.js";
import type { IOBPillData } from "#lib/types/status-pills.js";
import IOBPill from "./IOBPill.svelte";

/**
 * A device status carries an explicit `null` for a number the uploader had no
 * value for, and the pill data types declare those fields `number | undefined`
 * without anything enforcing it. The popover rows are built eagerly — StatusPill
 * reads `info.length` on every render — so a `null` reaching a `.toFixed` throws
 * during the dashboard's own render, and the authenticated layout's boundary
 * replaces the whole page with "Something went wrong".
 */
/** What a device status can actually produce, which the declared type forbids. */
type DeviceReported = Omit<IOBPillData, "basalIob" | "activity"> & {
  basalIob?: number | null;
  activity?: number | null;
};

const asPillData = (data: DeviceReported): IOBPillData =>
  // The null is the point: this asserts past the very lie the fix is about.
  data as IOBPillData;

const withNulls = (extra: Partial<DeviceReported>): IOBPillData =>
  asPillData({
    iob: 1.25,
    display: "1.25U",
    label: "IOB",
    info: [],
    level: "none",
    ...extra,
  });

describe("IOBPill absent device-status numbers", () => {
  it("still renders when basal IOB came through as null", async () => {
    render(IOBPill, {
      data: withNulls({ basalIob: null }),
    });

    await expect.element(page.getByText("1.25U")).toBeVisible();
  });

  it("still renders when insulin activity came through as null", async () => {
    render(IOBPill, {
      data: withNulls({ activity: null }),
    });

    await expect.element(page.getByText("1.25U")).toBeVisible();
  });

  it("omits the basal IOB row rather than inventing a value", async () => {
    const { container } = render(IOBPill, {
      data: withNulls({ basalIob: null }),
    });

    expect(container.textContent).not.toContain("Basal IOB");
    expect(container.textContent).not.toContain("0.00");
  });

  it("keeps the basal IOB row when the device did report one", async () => {
    render(IOBPill, { data: withNulls({ basalIob: 0.42 }) });

    await page.getByRole("button").first().click();
    await expect.element(page.getByText("Basal IOB")).toBeVisible();
    await expect.element(page.getByText("0.42U")).toBeVisible();
  });
});

describe("IOBPill last bolus time", () => {
  const at = new Date(2026, 0, 15, 14, 30).getTime();
  const withBolus = (): IOBPillData =>
    withNulls({ lastBolus: { mills: at, insulin: 1.5 } });

  afterEach(() => timeFormat.hydrate("12"));

  it("follows the 24-hour preference", async () => {
    timeFormat.hydrate("24");
    render(IOBPill, { data: withBolus() });

    await page.getByRole("button", { name: /IOB/ }).click();
    await expect.element(page.getByText(/@ 14:30/)).toBeVisible();
  });

  it("follows the 12-hour preference", async () => {
    timeFormat.hydrate("12");
    render(IOBPill, { data: withBolus() });

    await page.getByRole("button", { name: /IOB/ }).click();
    await expect.element(page.getByText(/@ 2:30\s?PM/i)).toBeVisible();
  });
});
