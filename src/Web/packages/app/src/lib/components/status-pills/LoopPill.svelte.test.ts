import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import type { LoopPillData } from "$lib/types/status-pills";
import LoopPill from "./LoopPill.svelte";

type LastEnacted = NonNullable<LoopPillData["lastEnacted"]>;

function loop(lastEnacted: Partial<LastEnacted>): LoopPillData {
  const now = Date.now();
  return {
    status: "enacted",
    symbol: "",
    loopName: "AAPS",
    lastLoopTime: now,
    display: "",
    label: "Loop",
    info: [],
    level: "none",
    lastEnacted: { time: now, type: "temp_basal", ...lastEnacted },
  };
}

async function openPopover() {
  await page.getByRole("button", { name: /AAPS · enacted/ }).click();
}

describe("LoopPill last enacted action", () => {
  it("leads an automatic bolus with its volume", async () => {
    render(LoopPill, { data: loop({ type: "bolus", bolusVolume: 0.3 }) });

    await openPopover();
    await expect.element(page.getByText("Automatic bolus")).toBeVisible();
    await expect.element(page.getByText(/0\.3U/)).toBeVisible();
  });

  it("says a temp basal was canceled", async () => {
    render(LoopPill, { data: loop({ type: "cancel" }) });

    await openPopover();
    await expect.element(page.getByText("Temp basal canceled")).toBeVisible();
  });

  it("states the rate and duration of a started temp basal", async () => {
    render(LoopPill, { data: loop({ type: "temp_basal", rate: 1.5, duration: 30 }) });

    await openPopover();
    await expect.element(page.getByText("Temp basal started")).toBeVisible();
    await expect.element(page.getByText(/1\.50U\/hour for 30m/)).toBeVisible();
  });
});
