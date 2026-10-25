import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import type { BodyWeight } from "$api";
import {
  remoteCommand,
  remoteQuery,
  updatedBy,
} from "$lib/test-stubs/remote-resource";

const entries: BodyWeight[] = [
  { _id: "bw-1", weightKg: 72.5, mills: 1_700_000_000_000 },
];

vi.mock("$api/generated/bodyWeights.generated.remote", () => ({
  getBodyWeights: () => remoteQuery(() => entries),
  create: remoteCommand(() => Promise.resolve()),
  deleteBodyWeight: remoteCommand(() => Promise.resolve()),
}));

import * as bw from "$api/generated/bodyWeights.generated.remote";
import WeightPage from "./+page.svelte";

describe("weight settings page", () => {
  beforeEach(() => {
    updatedBy(bw.create).length = 0;
    updatedBy(bw.deleteBodyWeight).length = 0;
  });

  it("refreshes the weight history the page reads when an entry is added", async () => {
    render(WeightPage, {});

    await page.getByRole("spinbutton").fill("80");
    await page.getByRole("button", { name: "Add entry" }).click();

    await vi.waitFor(() =>
      expect(updatedBy(bw.create)).toEqual([[bw.getBodyWeights]])
    );
  });

  it("refreshes the weight history the page reads when an entry is deleted", async () => {
    render(WeightPage, {});

    await page.getByRole("button", { name: "Delete entry" }).click();
    await page.getByRole("button", { name: "Delete", exact: true }).click();

    await vi.waitFor(() =>
      expect(updatedBy(bw.deleteBodyWeight)).toEqual([[bw.getBodyWeights]])
    );
  });
});
