import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import { MigrationJobState, MigrationMode } from "$api";
import type { MigrationJobInfo } from "$api";

let history: MigrationJobInfo[] = [];

// The factory is hoisted above every declaration in this file, so anything it evaluates eagerly
// has to be built inside it; only the lazy `history` read may reach out.
vi.mock("$api/generated/migrations.generated.remote", () => {
  const inertForm = { enhance: () => ({}), pending: 0, result: undefined };

  return {
    getPendingConfig: () => ({ run: () => Promise.resolve({ hasPendingConfig: false }) }),
    getSources: () => ({ run: () => Promise.resolve([]) }),
    getHistory: () => ({ run: () => Promise.resolve(history) }),
    getStatus: () => ({ run: () => Promise.resolve({}) }),
    testConnection: inertForm,
    startMigration: inertForm,
    cancelMigration: () => Promise.resolve(),
  };
});

import MigrationPage from "./+page.svelte";

const entry = (overrides: Partial<MigrationJobInfo>): MigrationJobInfo =>
  ({
    id: "11111111-1111-1111-1111-111111111111",
    mode: MigrationMode.Api,
    createdAt: "2026-09-01T00:00:00Z",
    sourceDescription: "https://mynightscout.example",
    state: MigrationJobState.Completed,
    startedAt: "2026-09-01T00:00:00Z",
    completedAt: "2026-09-01T00:00:10Z",
    ...overrides,
  }) as MigrationJobInfo;

// History lives behind a tab, and bits-ui renders only the active one.
const openHistory = async () => {
  const tab = page.getByRole("tab", { name: /History/ });
  await expect.element(tab).toBeVisible();
  await tab.click();
};

describe("settings/migration history", () => {
  // A read-only API secret cannot list sign-ins, so almost every hosted site finishes an import
  // with a message. Colouring the presence of a message as an error would flag them all.
  it("shows a skip-only summary without the error colour", async () => {
    history = [
      entry({
        errorMessage:
          "6 of 7 collections imported, 1 skipped. Skipped: listing the people and devices that can sign in needs an admin API secret.",
        hasFailures: false,
      }),
    ];

    render(MigrationPage, {});
    await openHistory();

    const summary = page.getByText(/6 of 7 collections imported/);
    await expect.element(summary).toBeVisible();
    await expect.element(summary).not.toHaveClass("text-destructive");
  });

  it("shows a summary naming a failed collection in the error colour", async () => {
    history = [
      entry({
        errorMessage: "1 of 2 collections imported, 1 failed. treatments: Nightscout answered 500 for treatments.",
        hasFailures: true,
      }),
    ];

    render(MigrationPage, {});
    await openHistory();

    const summary = page.getByText(/1 of 2 collections imported/);
    await expect.element(summary).toBeVisible();
    await expect.element(summary).toHaveClass("text-destructive");
  });
});
