import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { MigrationJobState, MigrationMode } from "$api";
import type { MigrationJobInfo } from "$api";

let history: MigrationJobInfo[] = [];
/** What a status poll answers; the history it leaves behind stands in for the server's. */
let polled: { status: object; history: MigrationJobInfo[] } | null = null;

const remote = vi.hoisted(() => ({
  historyCalls: 0,
  statusCalls: 0,
  statusError: undefined as Error | undefined,
  historyGate: undefined as Promise<void> | undefined,
  status: {} as Record<string, unknown>,
  startResult: undefined as { id: string } | undefined,
  enhanceCallback: undefined as undefined | ((args: { submit: () => Promise<void> }) => Promise<void>),
}));

// The factory is hoisted above every declaration in this file, so anything it evaluates eagerly
// has to be built inside it; only the lazy `history` read may reach out.
vi.mock("$api/generated/migrations.generated.remote", () => {
  const inertForm = { enhance: () => ({}), pending: 0, result: undefined };

  return {
    getPendingConfig: () => ({ run: () => Promise.resolve({ hasPendingConfig: false }) }),
    getSources: () => ({ run: () => Promise.resolve([]) }),
    getHistory: () => ({
      run: () => {
        remote.historyCalls += 1;
        return remote.historyGate ? remote.historyGate.then(() => history) : Promise.resolve(history);
      },
    }),
    getStatus: () => ({
      run: () => {
        remote.statusCalls += 1;
        if (remote.statusError) return Promise.reject(remote.statusError);
        if (!polled) return Promise.resolve(remote.status);
        history = polled.history;
        return Promise.resolve(polled.status);
      },
    }),
    testConnection: inertForm,
    startMigration: {
      pending: 0,
      get result() {
        return remote.startResult;
      },
      enhance: (callback: typeof remote.enhanceCallback) => {
        remote.enhanceCallback = callback;
        return {};
      },
    },
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
        errorMessage: "1 of 2 collections imported, 1 failed. treatments: Nightscout answered with a server error (500). It may be down or restarting; try again shortly.",
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

describe("settings/migration completion moment", () => {
  const moment = () => page.getByTestId("migration-complete");

  const finishRunning = (finished: Partial<MigrationJobInfo>) => {
    history = [entry({ state: MigrationJobState.Running, completedAt: undefined })];
    polled = {
      status: { jobId: entry({}).id, state: finished.state ?? MigrationJobState.Completed },
      history: [entry(finished)],
    };
  };

  it("paints when a watched run completes cleanly", async () => {
    finishRunning({ hasFailures: false });

    render(MigrationPage, {});

    await expect.element(moment()).toBeVisible();
    await expect.element(moment()).toHaveTextContent("Migration complete");
  });

  it("stays quiet when a watched run completes with a failed collection", async () => {
    finishRunning({ hasFailures: true, errorMessage: "1 of 2 collections imported, 1 failed." });

    render(MigrationPage, {});
    await openHistory();

    await expect.element(page.getByText(/1 of 2 collections imported/)).toBeVisible();
    await expect.element(moment()).not.toBeInTheDocument();
  });

  it.each([MigrationJobState.Failed, MigrationJobState.Cancelled, MigrationJobState.Interrupted])(
    "stays quiet when a watched run ends %s",
    async (state) => {
      finishRunning({ state });

      render(MigrationPage, {});
      await openHistory();

      await expect.element(page.getByRole("tabpanel")).toBeVisible();
      await expect.element(moment()).not.toBeInTheDocument();
    },
  );

  it("stays quiet for a run that had already completed before the page opened", async () => {
    history = [entry({ hasFailures: false })];

    render(MigrationPage, {});
    await openHistory();

    await expect.element(page.getByText("https://mynightscout.example")).toBeVisible();
    await expect.element(moment()).not.toBeInTheDocument();
  });
});

describe("settings/migration running job", () => {
  beforeEach(() => {
    polled = null;
    remote.historyCalls = 0;
    remote.statusCalls = 0;
    remote.statusError = undefined;
    remote.historyGate = undefined;
    remote.status = {
      jobId: "22222222-2222-2222-2222-222222222222",
      state: MigrationJobState.Running,
      progressPercentage: 42,
      currentOperation: "Copying entries",
    };
    remote.startResult = undefined;
    remote.enhanceCallback = undefined;
  });

  it("renders the page with progress while the job is still running", async () => {
    history = [
      entry({
        id: "22222222-2222-2222-2222-222222222222",
        state: MigrationJobState.Running,
      }),
    ];

    render(MigrationPage, {});

    await expect.element(page.getByRole("tab", { name: /History/ })).toBeVisible();
    await page.getByRole("tab", { name: /Progress/ }).click();
    await expect.element(page.getByText("Copying entries")).toBeVisible();
    await expect.element(page.getByText("42%")).toBeVisible();
  });

  it("switches to Progress as soon as a started job has an id", async () => {
    history = [];
    remote.startResult = { id: "22222222-2222-2222-2222-222222222222" };

    render(MigrationPage, {});
    await expect.element(page.getByRole("tab", { name: /History/ })).toBeVisible();

    await remote.enhanceCallback!({ submit: () => Promise.resolve() });

    await expect
      .element(page.getByRole("tab", { name: /Progress/ }))
      .toHaveAttribute("data-state", "active");
    await expect.element(page.getByText("Copying entries")).toBeVisible();
  });

  it("stops polling when the page is unmounted", async () => {
    history = [
      entry({
        id: "22222222-2222-2222-2222-222222222222",
        state: MigrationJobState.Running,
      }),
    ];

    const { unmount } = render(MigrationPage, {});
    await vi.waitFor(() => expect(remote.statusCalls).toBe(1));

    unmount();
    await new Promise((resolve) => setTimeout(resolve, 2500));

    expect(remote.statusCalls).toBe(1);
  });

  it("does not start polling when unmounted while the load is still in flight", async () => {
    history = [
      entry({
        id: "22222222-2222-2222-2222-222222222222",
        state: MigrationJobState.Running,
      }),
    ];
    let release!: () => void;
    remote.historyGate = new Promise<void>((resolve) => {
      release = resolve;
    });

    const { unmount } = render(MigrationPage, {});
    await vi.waitFor(() => expect(remote.historyCalls).toBe(1));
    unmount();
    release();
    await new Promise((resolve) => setTimeout(resolve, 2500));

    expect(remote.statusCalls).toBe(0);
  });

  it("shows the error when reading the job status fails", async () => {
    history = [
      entry({
        id: "22222222-2222-2222-2222-222222222222",
        state: MigrationJobState.Running,
      }),
    ];
    remote.statusError = new Error("status unavailable");

    render(MigrationPage, {});

    await expect.element(page.getByText("Failed to read migration status")).toBeVisible();
  });
});
