import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import { AlertRuleSeverity, type ClockAlertDto } from "#lib/api/generated/nocturne-api-client.js";
import ClockAlertBand from "./ClockAlertBand.svelte";

const firing: ClockAlertDto = {
  id: "e",
  ruleName: "Kitchen low",
  severity: AlertRuleSeverity.Critical,
  startedAt: new Date().toISOString(),
  acknowledged: false,
  seenHere: false,
};

describe("ClockAlertBand", () => {
  it("offers no Seen button where the face's owner did not allow it", async () => {
    render(ClockAlertBand, { alerts: [firing], now: Date.now() });

    await expect.element(page.getByText("Kitchen low")).toBeVisible();
    expect(page.getByRole("button", { name: "Seen" }).elements()).toHaveLength(0);
  });

  it("records that it was seen here", async () => {
    const seen: string[] = [];
    render(ClockAlertBand, {
      alerts: [firing],
      now: Date.now(),
      onSeen: async (id: string) => {
        seen.push(id);
      },
    });

    await page.getByRole("button", { name: "Seen" }).click();

    expect(seen).toEqual(["e"]);
  });

  it("says so once this clock has seen it", async () => {
    render(ClockAlertBand, {
      alerts: [{ ...firing, seenHere: true }],
      now: Date.now(),
      onSeen: async () => {},
    });

    await expect.element(page.getByText("Seen here")).toBeVisible();
    expect(page.getByRole("button", { name: "Seen" }).elements()).toHaveLength(0);
  });
});
