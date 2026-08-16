import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { page as pageState } from "$app/state";

const { toastError } = vi.hoisted(() => ({ toastError: vi.fn() }));

const nightStart = Date.UTC(2026, 2, 3, 2, 0);
const suggestion = {
  id: "22222222-2222-2222-2222-222222222222",
  nightOf: "2026-03-03",
  startMills: nightStart,
  endMills: nightStart + 45 * 60_000,
  confidence: 0.8,
  status: "Pending",
  lowestGlucose: 52,
  dropRate: 3,
  recoveryMinutes: 20,
};

// No entries, so the page skips the glucose chart and renders just the stats and
// the review actions this test is about.
const detail = { suggestion, entries: [] };
const suggestions = [suggestion];

let acceptImpl: () => Promise<unknown>;

vi.mock("$api/generated/compressionLows.generated.remote", () => ({
  getSuggestions: () => ({
    current: suggestions,
    loading: false,
    error: undefined,
    refresh: () => {},
  }),
  getSuggestion: () => ({ run: () => Promise.resolve(detail) }),
  acceptSuggestion: () => acceptImpl(),
  dismissSuggestion: () => Promise.resolve(),
  deleteSuggestion: () => Promise.resolve(),
  triggerDetection: () => Promise.resolve({}),
}));

vi.mock("svelte-sonner", () => ({ toast: { error: toastError } }));

import CompressionLowsPage from "./+page.svelte";

const detailLoaded = () => page.getByText("Recovery (min)");
const acceptButton = () => page.getByRole("button", { name: "Accept" });
const dismissButton = () => page.getByRole("button", { name: "Dismiss" });
const deleteButton = () => page.getByRole("button", { name: "Delete" });

describe("compression-lows page", () => {
  beforeEach(() => {
    acceptImpl = () => Promise.resolve({});
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
    acceptImpl = () => Promise.reject({ status: 403, body: { message: "Forbidden" } });

    render(CompressionLowsPage, {});

    await expect.element(acceptButton()).toBeVisible();
    await acceptButton().click();

    await vi.waitFor(() => expect(toastError).toHaveBeenCalled());
  });
});
