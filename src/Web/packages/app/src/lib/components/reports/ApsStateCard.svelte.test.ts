import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it } from "vitest";
import type { ApsSnapshot } from "#lib/api/index.js";
import ApsStateCard from "./ApsStateCard.svelte";

const snapshot = {
  mills: Date.UTC(2026, 10, 18, 17, 10),
  iob: 0.42,
  cob: 12,
  enacted: true,
  enactedRate: 0.58,
  enactedDuration: 30,
} as ApsSnapshot;

describe("ApsStateCard", () => {
  it("shows the algorithm's reason for the decision", async () => {
    const reason = "COB: 12, Dev: 5, BGI: -2; Eventual BG 95 < 100, temp 0.58 for 30m";
    render(ApsStateCard, { props: { snapshot: { ...snapshot, reason } as ApsSnapshot } });

    await expect.element(page.getByText("Reason", { exact: true })).toBeVisible();
    await expect.element(page.getByText(reason)).toBeVisible();
  });

  it("has no reason row when the snapshot carries none", async () => {
    render(ApsStateCard, { props: { snapshot } });

    await expect.element(page.getByText("0.42U")).toBeVisible();
    await expect.element(page.getByText("Reason", { exact: true })).not.toBeInTheDocument();
  });
});
