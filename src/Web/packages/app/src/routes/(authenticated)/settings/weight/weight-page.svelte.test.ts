import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import type { BodyWeight } from "#lib/api/index.js";
import {
  remoteCommand,
  remoteQuery,
  updatedBy,
} from "#lib/test-stubs/remote-resource.js";

const entries: BodyWeight[] = [
  { _id: "bw-1", weightKg: 72.5, mills: 1_700_000_000_000 },
];

const server = vi.hoisted(() => ({
  deleteAnswer: (): Promise<unknown> => Promise.resolve(),
}));

vi.mock("#lib/api/generated/bodyWeights.generated.remote.js", () => ({
  getBodyWeights: () => remoteQuery(() => entries),
  create: remoteCommand(() => Promise.resolve()),
  deleteBodyWeight: remoteCommand(() => server.deleteAnswer()),
}));

import * as bw from "#lib/api/generated/bodyWeights.generated.remote.js";
import WeightPage from "./+page.svelte";

describe("weight settings page", () => {
  beforeEach(() => {
    updatedBy(bw.create).length = 0;
    updatedBy(bw.deleteBodyWeight).length = 0;
    server.deleteAnswer = () => Promise.resolve();
  });

  it("refreshes the weight history the page reads when an entry is added", async () => {
    render(WeightPage, {});

    await page.getByRole("spinbutton").fill("80");
    await page.getByRole("button", { name: "Add entry" }).click();

    await vi.waitFor(() =>
      expect(updatedBy(bw.create)).toEqual([[bw.getBodyWeights]])
    );
  });

  it("takes a deleted entry off the list before the server answers", async () => {
    server.deleteAnswer = () => new Promise(() => {});
    render(WeightPage, {});

    await expect.element(page.getByText("72.5 kg")).toBeVisible();
    await page.getByRole("button", { name: "Delete entry" }).click();
    await page.getByRole("button", { name: "Delete", exact: true }).click();

    await expect.element(page.getByText("No entries yet")).toBeVisible();
    expect(page.getByText("72.5 kg").elements()).toHaveLength(0);
  });

  it("puts a deleted entry back when the server refuses", async () => {
    let refuse!: (err: unknown) => void;
    server.deleteAnswer = () => new Promise((_, reject) => (refuse = reject));
    render(WeightPage, {});

    await page.getByRole("button", { name: "Delete entry" }).click();
    await page.getByRole("button", { name: "Delete", exact: true }).click();
    await expect.element(page.getByText("No entries yet")).toBeVisible();

    refuse(Object.assign(new Error("boom"), { status: 500 }));

    await expect.element(page.getByText("72.5 kg")).toBeVisible();
    await expect.element(page.getByText("Failed to delete entry")).toBeVisible();
  });
});
