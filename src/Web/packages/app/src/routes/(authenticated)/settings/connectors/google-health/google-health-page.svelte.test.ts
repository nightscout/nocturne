import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import {
  GoogleHealthCategoryKind,
  GoogleHealthConnectionState,
  GoogleHealthFound,
  GoogleHealthSyncPhase,
  GoogleHealthTypeState,
  type GoogleHealthDataTypeStatus,
  type GoogleHealthStatus,
} from "$lib/api";
import { googleHealthMocks } from "$lib/test-stubs/google-health";

import GoogleHealthPage from "./google-health-page.svelte";

vi.mock(
  "$lib/api/generated/googleHealths.generated.remote",
  () => import("$lib/test-stubs/google-health")
);

function type(
  dataType: string,
  overrides: Partial<GoogleHealthDataTypeStatus> = {}
): GoogleHealthDataTypeStatus {
  return {
    dataType,
    state: GoogleHealthTypeState.NotScanned,
    found: GoogleHealthFound.NotScanned,
    count: 0,
    selectable: true,
    ...overrides,
  };
}

function status(
  overrides: Partial<GoogleHealthStatus> = {},
  types: GoogleHealthDataTypeStatus[] = [type("steps"), type("heart-rate")]
): GoogleHealthStatus {
  return {
    available: true,
    connectionState: GoogleHealthConnectionState.Disconnected,
    configured: false,
    connected: false,
    historyDays: 7,
    selectedTypes: ["steps", "heart-rate", "weight", "sleep"],
    previewRequired: false,
    categories: [
      {
        kind: GoogleHealthCategoryKind.Activity,
        hasSelectable: true,
        types: types.filter((entry) => entry.dataType !== "heart-rate"),
      },
      {
        kind: GoogleHealthCategoryKind.Vitals,
        hasSelectable: true,
        types: types.filter((entry) => entry.dataType === "heart-rate"),
      },
    ],
    ...overrides,
  };
}

const connected = (
  overrides: Partial<GoogleHealthStatus> = {},
  types?: GoogleHealthDataTypeStatus[]
) =>
  status(
    {
      configured: true,
      connected: true,
      connectionState: GoogleHealthConnectionState.Active,
      ...overrides,
    },
    types
  );

const refreshInventory = () =>
  page.getByRole("button", { name: "Refresh inventory", exact: true }).click();

describe("Google Health connector page", () => {
  beforeEach(() => {
    vi.resetAllMocks();
    googleHealthMocks.status.mockResolvedValue(status());
    googleHealthMocks.preview.mockResolvedValue(connected());
  });

  it("loads connection status without automatically scanning inventory", async () => {
    googleHealthMocks.status.mockResolvedValue(connected());
    render(GoogleHealthPage);
    await expect
      .element(page.getByRole("button", { name: "Disconnect", exact: true }))
      .toBeEnabled();
    await expect
      .poll(() => googleHealthMocks.status.mock.calls.length, { timeout: 10000 })
      .toBeGreaterThan(1);
    expect(googleHealthMocks.preview).not.toHaveBeenCalled();
    await refreshInventory();
    await expect.poll(() => googleHealthMocks.preview.mock.calls.length).toBe(1);
  });

  it("shows disconnect progress during a scan and discards its late failure", async () => {
    googleHealthMocks.status.mockResolvedValue(connected());
    let finishDisconnect!: (value: GoogleHealthStatus) => void;
    let failPreview!: (error: unknown) => void;
    googleHealthMocks.disconnect.mockImplementation(
      () => new Promise((resolve) => (finishDisconnect = resolve))
    );
    googleHealthMocks.preview.mockImplementation(
      () => new Promise((_, reject) => (failPreview = reject))
    );
    render(GoogleHealthPage);
    await refreshInventory();
    await page.getByRole("button", { name: "Disconnect", exact: true }).click();
    await expect
      .element(page.getByRole("button", { name: "Disconnecting…", exact: true }))
      .toBeDisabled();
    await expect
      .element(page.getByRole("status"))
      .toHaveTextContent("The current scan or import is being stopped.");
    const disconnected = status({ configured: true });
    googleHealthMocks.status.mockResolvedValue(disconnected);
    finishDisconnect(disconnected);
    failPreview(new Error("cancelled scan"));
    await expect
      .element(page.getByRole("button", { name: "Connect Google Health", exact: true }))
      .toBeEnabled();
    await expect
      .element(page.getByRole("status"))
      .toHaveTextContent("Your imported data has been kept.");
    await expect.element(page.getByRole("alert")).not.toBeInTheDocument();
    expect(googleHealthMocks.disconnect).toHaveBeenCalledTimes(1);
  });

  it("shows the server's pending disconnect after remounting without starting another scan", async () => {
    googleHealthMocks.status.mockResolvedValue(
      status({ configured: true, isDisconnecting: true })
    );
    render(GoogleHealthPage);
    await expect
      .element(page.getByRole("status"))
      .toHaveTextContent("Disconnecting Google Health");
    await expect
      .element(page.getByRole("button", { name: "Connect Google Health", exact: true }))
      .toBeDisabled();
    expect(googleHealthMocks.preview).not.toHaveBeenCalled();
    expect(googleHealthMocks.disconnect).not.toHaveBeenCalled();
  });

  it.each(["Sync now", "Save selection and import"])(
    "treats %s scheduling conflicts as coordination and polls completion",
    async (action) => {
      googleHealthMocks.status.mockResolvedValue(connected({ selectedTypes: ["steps"] }));
      googleHealthMocks.sync.mockRejectedValue({
        status: 400,
        body: { message: "already_running" },
      });
      render(GoogleHealthPage);
      const button = page.getByRole("button", { name: action, exact: true });
      await expect.element(button).toBeEnabled();
      googleHealthMocks.status.mockResolvedValue(
        connected({
          selectedTypes: ["steps"],
          isSyncing: true,
          syncPhase: GoogleHealthSyncPhase.Preparing,
        })
      );
      await button.click();
      await expect
        .element(
          page.getByText(
            "The import is running in the background. You can leave this page and return later.",
            { exact: true }
          )
        )
        .toBeVisible();
      await expect.element(page.getByRole("alert")).not.toBeInTheDocument();
      await expect.element(button).toBeDisabled();
      expect(googleHealthMocks.sync).toHaveBeenCalledTimes(1);
      googleHealthMocks.status.mockResolvedValue(connected({ selectedTypes: ["steps"] }));
      await expect
        .element(page.getByText("Google Health import completed.", { exact: true }))
        .toBeVisible();
      await expect.element(button).toBeEnabled();
    }
  );

  it("keeps a Sync now conflict recoverable when its immediate status refresh fails", async () => {
    googleHealthMocks.status.mockResolvedValue(connected({ selectedTypes: ["steps"] }));
    googleHealthMocks.sync.mockRejectedValue({
      status: 400,
      body: { message: "already_running" },
    });
    render(GoogleHealthPage);
    const button = page.getByRole("button", { name: "Sync now", exact: true });
    await expect.element(button).toBeEnabled();
    googleHealthMocks.status.mockRejectedValueOnce(new Error("temporary status failure"));
    await button.click();
    const unavailable = page.getByText(
      "Import status is temporarily unavailable. Retrying.",
      { exact: true }
    );
    await expect.element(unavailable).toBeVisible();
    await expect.element(page.getByRole("alert")).not.toBeInTheDocument();
    googleHealthMocks.status.mockResolvedValue(
      connected({
        selectedTypes: ["steps"],
        isSyncing: true,
        syncPhase: GoogleHealthSyncPhase.Preparing,
      })
    );
    await expect.element(page.getByRole("progressbar")).toBeVisible();
    await expect.element(unavailable).not.toBeInTheDocument();
  });

  it("polls progress and completion without a websocket event", async () => {
    googleHealthMocks.status.mockResolvedValue(
      connected({
        importFrom: "2026-08-01T00:00:00Z",
        isSyncing: true,
        syncPhase: GoogleHealthSyncPhase.Preparing,
      })
    );
    render(GoogleHealthPage);
    await expect.element(page.getByText("Preparing the import")).toBeVisible();
    googleHealthMocks.status.mockResolvedValue(
      connected({
        isSyncing: true,
        syncPhase: GoogleHealthSyncPhase.Reading,
        syncDataType: "steps",
        syncProgressPercent: 22,
        syncPagesRead: 12,
        importFrom: "2026-08-01T00:00:00Z",
      })
    );
    await expect
      .element(page.getByRole("progressbar", { name: "Google Health import progress" }))
      .toHaveAttribute("aria-valuenow", "22");
    await expect.element(page.getByText("Reading Steps")).toBeVisible();
    await expect
      .element(page.getByText("12 Google pages read for this data type"))
      .toBeVisible();
    googleHealthMocks.status.mockResolvedValue(
      connected({ importFrom: undefined, backfillComplete: true })
    );
    await expect
      .element(page.getByText("Google Health import completed.", { exact: true }))
      .toBeVisible();
    await expect.element(page.getByRole("progressbar")).not.toBeInTheDocument();
    await expect.element(page.getByLabelText("Import data from")).toHaveValue("");
    await page.getByRole("button", { name: "Save import settings" }).click();
    await expect.poll(() => googleHealthMocks.save.mock.calls.length).toBe(1);
    expect(googleHealthMocks.save).toHaveBeenCalledWith(
      expect.objectContaining({ importFrom: null })
    );
  });

  it("preserves an unsaved import date when polling completes a sync", async () => {
    googleHealthMocks.status.mockResolvedValue(
      connected({ importFrom: "2026-08-01T00:00:00Z" })
    );
    googleHealthMocks.sync.mockResolvedValue(
      connected({ importFrom: "2026-08-01T00:00:00Z", isSyncing: true })
    );
    render(GoogleHealthPage);

    const importFrom = page.getByLabelText("Import data from");
    await expect.element(importFrom).toHaveValue("2026-08-01");
    await importFrom.fill("2026-09-01");
    await page.getByRole("button", { name: "Sync now" }).click();

    googleHealthMocks.status.mockResolvedValue(
      connected({ importFrom: undefined, backfillComplete: true })
    );
    await expect
      .element(page.getByText("Google Health import completed.", { exact: true }))
      .toBeVisible();
    await expect.element(importFrom).toHaveValue("2026-09-01");
  });

  it("keeps category expansion choices while status polling refreshes", async () => {
    googleHealthMocks.status.mockResolvedValue(connected());
    render(GoogleHealthPage);

    const vitals = page.getByTestId("google-health-category-vitals");
    await expect.element(vitals).toHaveAttribute("open");
    await page.getByText("Vitals", { exact: true }).click();
    await expect.element(vitals).not.toHaveAttribute("open");

    const statusCalls = googleHealthMocks.status.mock.calls.length;
    googleHealthMocks.status.mockResolvedValue(
      connected({
        isSyncing: true,
        syncPhase: GoogleHealthSyncPhase.Reading,
        syncProgressPercent: 22,
      })
    );
    await expect
      .poll(() => googleHealthMocks.status.mock.calls.length, { timeout: 10000 })
      .toBeGreaterThan(statusCalls);
    await expect
      .element(page.getByRole("progressbar", { name: "Google Health import progress" }))
      .toHaveAttribute("aria-valuenow", "22");
    await expect.element(vitals).not.toHaveAttribute("open");
  });

  it("connects with one click, saving the default selection and seven-day history", async () => {
    googleHealthMocks.start.mockResolvedValue({ url: "" });
    render(GoogleHealthPage);
    await page.getByRole("button", { name: "Connect Google Health", exact: true }).click();
    await expect.poll(() => googleHealthMocks.start.mock.calls.length).toBe(1);
    expect(googleHealthMocks.save).toHaveBeenCalledWith({
      dataTypes: ["steps", "heart-rate", "weight", "sleep"],
      historyDays: 7,
      importFrom: null,
      previewOnly: true,
    });
  });

  it("reconnects a saved connection without overwriting its settings", async () => {
    googleHealthMocks.status.mockResolvedValue(status({ configured: true }));
    googleHealthMocks.start.mockResolvedValue({ url: "" });
    render(GoogleHealthPage);

    await page.getByRole("button", { name: "Connect Google Health", exact: true }).click();

    await expect.poll(() => googleHealthMocks.start.mock.calls.length).toBe(1);
    expect(googleHealthMocks.save).not.toHaveBeenCalled();
  });

  it("explains that the operator has not enabled Google Health", async () => {
    googleHealthMocks.status.mockResolvedValue(
      status({ available: false, connectionState: GoogleHealthConnectionState.Unavailable })
    );
    render(GoogleHealthPage);
    await expect
      .element(
        page.getByText("Your Nocturne operator hasn't enabled Google Health on this server.")
      )
      .toBeVisible();
    await expect
      .element(page.getByRole("link", { name: "How Google Health is enabled" }))
      .toHaveAttribute("href", "https://getnocturne.dev/docs/data-sources/google-health");
    await expect
      .element(page.getByRole("button", { name: "Connect Google Health" }))
      .not.toBeInTheDocument();
    await expect.element(page.getByText("Unavailable", { exact: true })).toBeVisible();
  });

  it("pauses imports for a connected tenant once the operator turns Google Health off", async () => {
    googleHealthMocks.status.mockResolvedValue(
      connected({
        available: false,
        connectionState: GoogleHealthConnectionState.Unavailable,
        selectedTypes: ["steps"],
      })
    );
    render(GoogleHealthPage);
    await expect
      .element(page.getByText("imports are paused because your Nocturne operator", { exact: false }))
      .toBeVisible();
    await expect.element(page.getByText("Unavailable", { exact: true })).toBeVisible();
    for (const name of ["Sync now", "Save selection and import", "Refresh inventory"])
      await expect.element(page.getByRole("button", { name, exact: true })).toBeDisabled();
    await expect
      .element(page.getByRole("button", { name: "Disconnect", exact: true }))
      .toBeEnabled();
  });

  it("renders the backend's per-type state with frontend labels", async () => {
    googleHealthMocks.status.mockResolvedValue(
      connected({}, [
        type("steps", {
          state: GoogleHealthTypeState.Importing,
          found: GoogleHealthFound.Found,
          count: 42,
          destination: "step-counts",
        }),
        type("heart-rate", {
          state: GoogleHealthTypeState.ReadFailed,
          found: GoogleHealthFound.ScanFailed,
          errorCode: "google_unavailable",
          selectable: false,
        }),
        type("menstrual-period", {
          state: GoogleHealthTypeState.NoReadonlyScope,
          selectable: false,
        }),
      ])
    );
    render(GoogleHealthPage);

    const steps = page.getByRole("row", { name: /Steps/ });
    await expect.element(steps).toHaveTextContent("Yes (42)");
    await expect.element(steps).toHaveTextContent("Step history");
    await expect.element(steps).toHaveTextContent("Import enabled");
    const heart = page.getByRole("row", { name: /Heart rate/ });
    await expect.element(heart).toHaveTextContent("Scan failed");
    await expect.element(heart).toHaveTextContent("Read failed (google_unavailable)");
    await expect
      .element(page.getByRole("row", { name: /Menstrual period/ }))
      .toHaveTextContent("Google Health currently provides no read-only scope for this type.");
    await expect
      .element(page.getByRole("checkbox", { name: "Import Menstrual period" }))
      .toBeDisabled();
  });

  it("shows the scanned inventory the preview returns", async () => {
    googleHealthMocks.status.mockResolvedValue(connected());
    googleHealthMocks.preview.mockResolvedValue(
      connected({}, [
        type("steps"),
        type("heart-rate", {
          state: GoogleHealthTypeState.Available,
          found: GoogleHealthFound.Found,
          count: 7,
        }),
      ])
    );
    render(GoogleHealthPage);
    await refreshInventory();
    await expect
      .element(page.getByRole("row", { name: /Heart rate/ }))
      .toHaveTextContent("Yes (7)");
  });

  it.each(["already_running", "google_unavailable"])(
    "clears stale inventory feedback after retrying %s",
    async (errorCode) => {
      googleHealthMocks.status.mockResolvedValue(connected());
      googleHealthMocks.preview
        .mockRejectedValueOnce({
          status: errorCode === "already_running" ? 400 : 502,
          body: { message: errorCode },
        })
        .mockResolvedValue(
          connected({}, [
            type("steps"),
            type("heart-rate", { found: GoogleHealthFound.Found, count: 42 }),
          ])
        );
      render(GoogleHealthPage);
      await refreshInventory();
      const feedback = page.getByRole(errorCode === "already_running" ? "status" : "alert");
      await expect.element(feedback).toBeVisible();
      await refreshInventory();
      await expect
        .element(page.getByRole("row", { name: /Heart rate/ }))
        .toHaveTextContent("42");
      await expect.element(feedback).not.toBeInTheDocument();
    }
  );

  it("allows an unselectable selected type to be unchecked and saved without syncing", async () => {
    googleHealthMocks.status.mockResolvedValue(
      connected({ selectedTypes: ["steps", "heart-rate"] }, [
        type("steps"),
        type("heart-rate", { state: GoogleHealthTypeState.ReadFailed, selectable: false }),
      ])
    );
    render(GoogleHealthPage);
    await page.getByRole("checkbox", { name: "Import Heart rate" }).click();
    await page.getByRole("button", { name: "Save import settings", exact: true }).click();
    await expect.poll(() => googleHealthMocks.save.mock.calls.length).toBe(1);
    expect(googleHealthMocks.save).toHaveBeenCalledWith(
      expect.objectContaining({ dataTypes: ["steps"] })
    );
    expect(googleHealthMocks.sync).not.toHaveBeenCalled();
  });

  it("saves an older history date and an empty selection without reconnecting", async () => {
    googleHealthMocks.status
      .mockResolvedValueOnce(
        connected({ selectedTypes: ["heart-rate"], importFrom: "2026-08-29T00:00:00Z" })
      )
      .mockResolvedValue(
        connected({
          selectedTypes: [],
          importFrom: "2020-01-01T00:00:00Z",
          connectionState: GoogleHealthConnectionState.NothingSelected,
        })
      );
    render(GoogleHealthPage);
    await page.getByRole("checkbox", { name: "Import Heart rate" }).click();
    await page.getByLabelText("Import data from").fill("2020-01-01");
    await page.getByRole("button", { name: "Save import settings", exact: true }).click();
    await expect.poll(() => googleHealthMocks.save.mock.calls.length).toBe(1);
    expect(googleHealthMocks.save).toHaveBeenCalledWith(
      expect.objectContaining({ dataTypes: [], importFrom: "2020-01-01T00:00:00.000Z" })
    );
    expect(googleHealthMocks.sync).not.toHaveBeenCalled();
    expect(googleHealthMocks.start).not.toHaveBeenCalled();
    await expect
      .element(
        page.getByText(
          "Google Health is connected. Imports are paused because no data types are selected."
        )
      )
      .toBeVisible();
    await expect
      .element(page.getByRole("button", { name: "Sync now", exact: true }))
      .toBeDisabled();
  });

  it("asks for review before importing", async () => {
    googleHealthMocks.status.mockResolvedValue(
      connected({
        previewRequired: true,
        connectionState: GoogleHealthConnectionState.ReviewRequired,
      })
    );
    render(GoogleHealthPage);
    await expect
      .element(page.getByText("Review the available data below", { exact: false }))
      .toBeVisible();
    await expect.element(page.getByRole("button", { name: "Sync now" })).toBeDisabled();
    await expect
      .element(page.getByRole("button", { name: "Save selection and import" }))
      .toBeEnabled();
  });

  it("shows persisted historical progress after reloading", async () => {
    googleHealthMocks.status.mockResolvedValue(
      connected({ backfillSyncedThrough: "2025-06-01T00:00:00.000Z", backfillComplete: false })
    );
    render(GoogleHealthPage);
    await expect
      .element(page.getByText("Historical import progress", { exact: true }))
      .toBeVisible();
    await expect
      .element(page.getByText("Synchronized back through 2025-06-01.", { exact: false }))
      .toBeVisible();
  });

  it("links the shared connector reset page from the connection", async () => {
    googleHealthMocks.status.mockResolvedValue(connected());
    render(GoogleHealthPage);
    await expect.element(page.getByText("Import recovery", { exact: true })).toBeVisible();
    await expect
      .element(page.getByRole("link", { name: "Open connector reset" }))
      .toHaveAttribute("href", "/settings/admin/connector-cursors");
  });

  it("asks for review rather than promising automatic sync when an error is also reported", async () => {
    googleHealthMocks.status.mockResolvedValue(
      connected({
        previewRequired: true,
        errorCode: "partial_consent",
        connectionState: GoogleHealthConnectionState.ReviewRequired,
      })
    );
    render(GoogleHealthPage);
    await expect
      .element(page.getByText("Review the available data below", { exact: false }))
      .toBeVisible();
    await expect
      .element(page.getByText("Not every requested permission was granted.", { exact: true }))
      .toBeVisible();
    await expect
      .element(page.getByText("Automatic sync runs approximately every 15 minutes", { exact: false }))
      .not.toBeInTheDocument();
  });

  it("shows a running import as paused once the operator turns Google Health off", async () => {
    googleHealthMocks.status.mockResolvedValue(
      connected({
        available: false,
        isSyncing: true,
        syncPhase: GoogleHealthSyncPhase.Reading,
        connectionState: GoogleHealthConnectionState.Unavailable,
      })
    );
    render(GoogleHealthPage);
    await expect.element(page.getByText("Unavailable", { exact: true })).toBeVisible();
    await expect
      .element(page.getByText("imports are paused because your Nocturne operator", { exact: false }))
      .toBeVisible();
  });

  it("explains a session the server's new Google client cannot use", async () => {
    googleHealthMocks.status.mockResolvedValue(
      status({ configured: true, errorCode: "client_changed" })
    );
    render(GoogleHealthPage);
    await expect
      .element(page.getByText("This server's Google client has changed", { exact: false }))
      .toBeVisible();
    await expect
      .element(page.getByRole("button", { name: "Connect Google Health", exact: true }))
      .toBeEnabled();
  });

  describe("after the sign-in redirect", () => {
    const path = location.pathname;
    afterEach(() => history.replaceState(null, "", path));

    it("explains the specific reason the callback carried", async () => {
      history.replaceState(null, "", `${path}?connection=account_mismatch`);
      render(GoogleHealthPage);
      await expect
        .element(page.getByRole("alert"))
        .toHaveTextContent(
          "This is a different Google account. Purge the old import before switching."
        );
    });

    it("ignores a value it has no sentence for", async () => {
      history.replaceState(null, "", `${path}?connection=%3Cb%3Eforged%3C%2Fb%3E`);
      render(GoogleHealthPage);
      await expect
        .element(page.getByRole("button", { name: "Connect Google Health", exact: true }))
        .toBeEnabled();
      await expect.element(page.getByRole("alert")).not.toBeInTheDocument();
    });
  });
});
