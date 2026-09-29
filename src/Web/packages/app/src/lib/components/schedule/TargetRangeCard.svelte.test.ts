import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";

const profiles = vi.hoisted(() => ({ create: vi.fn() }));
vi.mock("$api/generated/profiles.generated.remote", () => ({
  createTargetRangeSchedule: profiles.create,
}));

import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
import TargetRangeCard from "./TargetRangeCard.svelte";

const schedule = { profileName: "Default", entries: [{ time: "00:00", low: 90, high: 144 }] };

beforeEach(() => {
  glucoseUnits.current = "mmol";
  profiles.create.mockReset().mockResolvedValue({});
});

describe("TargetRangeCard", () => {
  it("keeps the low and high fields after both are cleared, and saves once they are filled again", async () => {
    render(TargetRangeCard, { profileName: "Default", schedule });
    await page.getByRole("button", { name: "Edit" }).click();

    const low = page.getByRole("spinbutton", { name: "Low from 00:00" });
    const high = page.getByRole("spinbutton", { name: "High from 00:00" });
    await userEvent.clear(low);
    await userEvent.clear(high);
    await userEvent.tab();

    await expect.element(low).toBeVisible();
    await expect.element(high).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Save" })).toBeDisabled();

    await userEvent.fill(low, "5");
    await userEvent.fill(high, "8");
    await userEvent.tab();
    await page.getByRole("button", { name: "Save" }).click();

    await expect.poll(() => profiles.create.mock.calls.length).toBe(1);
    const [entry] = profiles.create.mock.calls[0][0].entries;
    expect(entry.low).toBeCloseTo(90, 0);
    expect(entry.high).toBeCloseTo(144, 0);
  });
});
