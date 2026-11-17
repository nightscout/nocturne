import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { error } from "@sveltejs/kit";
import { page as pageState } from "$app/state";
import { remoteCommand, remoteQuery, updatedBy } from "#lib/test-stubs/remote-resource.js";

const { toastError } = vi.hoisted(() => ({ toastError: vi.fn() }));

const nightStart = Date.UTC(2026, 2, 3, 2, 0);
const suggestion = {
  id: "22222222-2222-2222-2222-222222222222",
  nightOf: "2026-03-03",
  startMills: nightStart,
  endMills: nightStart + 45 * 60_000,
  confidence: 0.8,
  confidenceBand: "High",
  status: "Pending",
  lowestGlucose: 52,
  dropRate: 3,
  recoveryMinutes: 20,
};

// No entries, so the page skips the glucose chart and renders just the stats and
// the review actions this test is about.
const detail = { suggestion, entries: [] };
const otherSuggestion = {
  ...suggestion,
  id: "33333333-3333-3333-3333-333333333333",
  nightOf: "2026-03-04",
  confidence: 0.7,
  confidenceBand: "Medium",
};
let suggestions = [suggestion];
const suggestionsQuery = remoteQuery(() => suggestions);

let reviewImpl: (arg: unknown) => Promise<unknown>;

vi.mock("#lib/api/generated/compressionLows.generated.remote.js", () => ({
  getSuggestions: () => suggestionsQuery,
  getSuggestion: () => remoteQuery(() => detail),
  reviewSuggestions: remoteCommand((arg) => reviewImpl(arg)),
  triggerDetection: remoteCommand(() => ({})),
}));

vi.mock("svelte-sonner", () => ({ toast: { error: toastError } }));

import { reviewSuggestions } from "#lib/api/generated/compressionLows.generated.remote.js";
import CompressionLowsPage from "./+page.svelte";

const detailLoaded = () => page.getByText("Recovery (min)");
const acceptButton = () => page.getByRole("button", { name: "Accept" });
const dismissButton = () => page.getByRole("button", { name: "Dismiss" });
const deleteButton = () => page.getByRole("button", { name: "Delete" });

describe("compression-lows page", () => {
  beforeEach(() => {
    reviewImpl = () => Promise.resolve();
    suggestions = [suggestion];
    toastError.mockClear();
    pageState.data = {};
  });

  it("hides the review controls from a member without glucose.readwrite", async () => {
    pageState.data = { effectivePermissions: ["glucose.read"] };

    render(CompressionLowsPage, {});

    // Anchor on the detail card first: it loads asynchronously, so an absence
    // assertion would otherwise pass before the actions could ever render.
    await expect.element(detailLoaded()).toBeVisible();
    await expect.element(acceptButton()).not.toBeInTheDocument();
    await expect.element(dismissButton()).not.toBeInTheDocument();
    await expect.element(deleteButton()).not.toBeInTheDocument();
  });

  it("shows the review controls to a member holding glucose.readwrite", async () => {
    pageState.data = { effectivePermissions: ["glucose.readwrite"] };

    render(CompressionLowsPage, {});

    await expect.element(detailLoaded()).toBeVisible();
    await expect.element(acceptButton()).toBeVisible();
    await expect.element(dismissButton()).toBeVisible();
    await expect.element(deleteButton()).toBeVisible();
  });

  it("surfaces a refused accept instead of discarding it", async () => {
    pageState.data = { effectivePermissions: ["glucose.readwrite"] };
    reviewImpl = async () => error(403, "Forbidden");

    render(CompressionLowsPage, {});

    await expect.element(acceptButton()).toBeVisible();
    await acceptButton().click();

    await vi.waitFor(() =>
      expect(toastError).toHaveBeenCalledWith(
        "Reviewing compression lows requires the glucose.readwrite permission."
      )
    );
  });

  it("takes a deleted suggestion off the page before the server answers", async () => {
    pageState.data = { effectivePermissions: ["glucose.readwrite"] };
    let answer!: () => void;
    reviewImpl = () => new Promise<void>((resolve) => (answer = resolve));

    render(CompressionLowsPage, {});
    await expect.element(deleteButton()).toBeVisible();
    await deleteButton().click();

    try {
      await expect.element(page.getByText("No compression lows detected yet")).toBeVisible();
      expect(updatedBy(reviewSuggestions).at(-1)).toHaveLength(1);
    } finally {
      answer();
    }
  });

  it("puts the suggestion back and says why when the batch is refused", async () => {
    pageState.data = { effectivePermissions: ["glucose.readwrite"] };
    reviewImpl = async () => error(403, "Forbidden");

    render(CompressionLowsPage, {});
    await expect.element(deleteButton()).toBeVisible();
    await deleteButton().click();

    await vi.waitFor(() => expect(toastError).toHaveBeenCalled());
    await expect.element(page.getByText("No compression lows detected yet")).not.toBeInTheDocument();
    await expect.element(detailLoaded()).toBeVisible();
  });

  it("sends the selected suggestions and the action in one command", async () => {
    pageState.data = { effectivePermissions: ["glucose.readwrite"] };
    suggestions = [suggestion, otherSuggestion];
    const sent = vi.fn(() => Promise.resolve());
    reviewImpl = sent;

    render(CompressionLowsPage, {});
    const rows = page.getByRole("button", { name: /Pending review/ });
    await rows.first().click();
    await rows.last().click({ modifiers: ["Shift"] });
    await page.getByRole("button", { name: "Dismiss all" }).click();

    await vi.waitFor(() => expect(sent).toHaveBeenCalledTimes(1));
    expect(sent).toHaveBeenCalledWith({
      action: "Dismiss",
      items: [{ id: suggestion.id }, { id: otherSuggestion.id }],
    });
  });

  it("labels each suggestion with the band the API served", async () => {
    suggestionsQuery.set([suggestion, otherSuggestion]);

    render(CompressionLowsPage, {});

    const rows = page.getByRole("button", { name: /Pending review/ });
    const high = rows.first().getByText("High", { exact: true });
    const medium = rows.last().getByText("Medium", { exact: true });
    await expect.element(high).toBeVisible();
    await expect.element(medium).toBeVisible();
    expect(high.element().className).toContain("bg-primary");
    expect(medium.element().className).toContain("bg-secondary");
  });

  it("takes the label from the served band, not from the score", async () => {
    suggestionsQuery.set([{ ...suggestion, confidence: 0.1, confidenceBand: "High" }]);

    render(CompressionLowsPage, {});

    await expect
      .element(page.getByRole("button", { name: /Pending review/ }).getByText("High", { exact: true }))
      .toBeVisible();
  });
});
