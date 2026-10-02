import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { GoogleHealthSyncPhase, type GoogleHealthStatus } from "$lib/api";
import { googleHealthMocks } from "$lib/test-stubs/google-health";

import GoogleHealthPage from "./google-health-page.svelte";

vi.mock(
  "$lib/api/generated/googleHealths.generated.remote",
  () => import("$lib/test-stubs/google-health")
);
vi.mock("$api/generated/patientRecords.generated.remote", () => ({
  getPatientRecord: () => ({ current: null }),
}));

function status(
  overrides: Partial<GoogleHealthStatus> = {}
): GoogleHealthStatus {
  return {
    configured: false,
    connected: false,
    clientId: "",
    callbackUrl: "",
    historyDays: 7,
    selectedTypes: ["steps", "heart-rate", "weight", "sleep"],
    grantedTypes: [],
    previewRequired: false,
    capabilities: [
      {
        dataType: "steps",
        displayName: "Steps",
        category: "Activity",
        supported: true,
        destination: "step-counts",
      },
      {
        dataType: "heart-rate",
        displayName: "Heart rate",
        category: "Vitals",
        supported: true,
        destination: "heart-rates",
      },
      {
        dataType: "weight",
        displayName: "Weight",
        category: "Body measurement",
        supported: true,
        destination: "body-weights",
      },
      {
        dataType: "body-fat",
        displayName: "Body fat",
        category: "Body measurement",
        supported: false,
      },
    ],
    ...overrides,
  };
}

describe("Google Health connector page", () => {
  it.each(["Sync now", "Save selection and import"])(
    "treats %s scheduling conflicts as coordination and polls completion",
    async (action) => {
      googleHealthMocks.status.mockResolvedValue(
        status({ configured: true, connected: true, selectedTypes: ["steps"] })
      );
      googleHealthMocks.sync.mockRejectedValue({
        status: 409,
        body: { message: "already_running" },
      });
      render(GoogleHealthPage);
      const button = page.getByRole("button", { name: action, exact: true });
      await expect.element(button).toBeEnabled();
      googleHealthMocks.status.mockResolvedValue(
        status({
          configured: true,
          connected: true,
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
      googleHealthMocks.status.mockResolvedValue(
        status({
          configured: true,
          connected: true,
          selectedTypes: ["steps"],
          isSyncing: false,
        })
      );
      await expect
        .element(
          page.getByText("Google Health import completed.", { exact: true })
        )
        .toBeVisible();
      await expect.element(button).toBeEnabled();
    }
  );

  it("keeps a Sync now conflict recoverable when its immediate status refresh fails", async () => {
    googleHealthMocks.status.mockResolvedValue(
      status({ configured: true, connected: true, selectedTypes: ["steps"] })
    );
    googleHealthMocks.sync.mockRejectedValue({
      status: 409,
      body: { message: "already_running" },
    });
    render(GoogleHealthPage);
    const button = page.getByRole("button", { name: "Sync now", exact: true });
    await expect.element(button).toBeEnabled();
    googleHealthMocks.status.mockRejectedValueOnce(
      new Error("temporary status failure")
    );
    await button.click();
    await expect
      .element(
        page.getByText("Import status is temporarily unavailable. Retrying.", {
          exact: true,
        })
      )
      .toBeVisible();
    await expect.element(page.getByRole("alert")).not.toBeInTheDocument();
    googleHealthMocks.status.mockResolvedValue(
      status({
        configured: true,
        connected: true,
        selectedTypes: ["steps"],
        isSyncing: true,
        syncPhase: GoogleHealthSyncPhase.Preparing,
      })
    );
    await expect.element(page.getByRole("progressbar")).toBeVisible();
    await expect
      .element(
        page.getByText("Import status is temporarily unavailable. Retrying.", {
          exact: true,
        })
      )
      .not.toBeInTheDocument();
  });

  it("polls progress and completion without a websocket event", async () => {
    googleHealthMocks.status.mockResolvedValue(
      status({
        configured: true,
        connected: true,
        importFrom: "2026-08-01T00:00:00Z",
        isSyncing: true,
        syncPhase: GoogleHealthSyncPhase.Preparing,
      })
    );
    render(GoogleHealthPage);
    await expect.element(page.getByText("Preparing the import")).toBeVisible();
    googleHealthMocks.status.mockResolvedValue(
      status({
        configured: true,
        connected: true,
        isSyncing: true,
        syncPhase: GoogleHealthSyncPhase.Reading,
        syncDataType: "steps",
        syncProgressPercent: 22,
        syncPagesRead: 12,
        importFrom: "2026-08-01T00:00:00Z",
      })
    );
    await expect
      .element(
        page.getByRole("progressbar", { name: "Google Health import progress" })
      )
      .toHaveAttribute("aria-valuenow", "22");
    googleHealthMocks.status.mockResolvedValue(
      status({
        configured: true,
        connected: true,
        isSyncing: false,
        importFrom: undefined,
        backfillComplete: true,
      })
    );
    await expect
      .element(
        page.getByText("Google Health import completed.", { exact: true })
      )
      .toBeVisible();
    await expect.element(page.getByRole("progressbar")).not.toBeInTheDocument();
    await expect
      .element(page.getByLabelText("Import data from"))
      .toHaveValue("");
    await page.getByRole("button", { name: "Save import settings" }).click();
    await expect.poll(() => googleHealthMocks.save.mock.calls.length).toBe(1);
    expect(googleHealthMocks.save).toHaveBeenCalledWith(
      expect.objectContaining({ importFrom: null })
    );
  });

  it("preserves an unsaved import date when polling completes a sync", async () => {
    googleHealthMocks.status.mockResolvedValue(
      status({
        configured: true,
        connected: true,
        importFrom: "2026-08-01T00:00:00Z",
      })
    );
    googleHealthMocks.sync.mockResolvedValue(
      status({
        configured: true,
        connected: true,
        importFrom: "2026-08-01T00:00:00Z",
        isSyncing: true,
      })
    );
    render(GoogleHealthPage);

    const importFrom = page.getByLabelText("Import data from");
    await expect.element(importFrom).toHaveValue("2026-08-01");
    await importFrom.fill("2026-09-01");
    await page.getByRole("button", { name: "Sync now" }).click();

    googleHealthMocks.status.mockResolvedValue(
      status({
        configured: true,
        connected: true,
        importFrom: undefined,
        backfillComplete: true,
      })
    );
    await expect
      .element(
        page.getByText("Google Health import completed.", { exact: true })
      )
      .toBeVisible();
    await expect.element(importFrom).toHaveValue("2026-09-01");
  });

  it("keeps category expansion choices while status polling refreshes", async () => {
    googleHealthMocks.status.mockResolvedValue(
      status({ configured: true, connected: true })
    );
    googleHealthMocks.preview.mockResolvedValue({
      items: [
        { dataType: "steps", granted: true, supported: true, count: 4 },
        { dataType: "heart-rate", granted: true, supported: true, count: 6 },
      ],
    });
    render(GoogleHealthPage);

    const vitals = page.getByTestId("google-health-category-Vitals");
    await expect.element(vitals).toHaveAttribute("open");
    await page.getByText("Vitals", { exact: true }).click();
    await expect.element(vitals).not.toHaveAttribute("open");

    const statusCalls = googleHealthMocks.status.mock.calls.length;
    googleHealthMocks.status.mockResolvedValue(
      status({
        configured: true,
        connected: true,
        isSyncing: true,
        syncPhase: GoogleHealthSyncPhase.Reading,
        syncProgressPercent: 22,
      })
    );
    await expect
      .poll(() => googleHealthMocks.status.mock.calls.length, {
        timeout: 10000,
      })
      .toBeGreaterThan(statusCalls);
    await expect
      .element(
        page.getByRole("progressbar", { name: "Google Health import progress" })
      )
      .toHaveAttribute("aria-valuenow", "22");
    await expect.element(vitals).not.toHaveAttribute("open");
  });

  beforeEach(() => {
    vi.resetAllMocks();
    googleHealthMocks.status.mockResolvedValue(status());
    googleHealthMocks.preview.mockResolvedValue({ items: [] });
  });

  it("shows detected, supported, and unsupported data types", async () => {
    googleHealthMocks.status.mockResolvedValue(
      status({ configured: true, connected: true })
    );
    googleHealthMocks.preview.mockResolvedValue({
      items: [
        { dataType: "steps", granted: true, count: 42, supported: true },
        { dataType: "body-fat", granted: true, count: 3, supported: false },
      ],
    });
    render(GoogleHealthPage);
    await expect
      .element(page.getByRole("heading", { name: "Google Health" }))
      .toBeVisible();
    await expect.element(page.getByLabelText("Import data from")).toBeVisible();
    await expect
      .element(page.getByText("Import enabled", { exact: true }))
      .toBeVisible();
    await page.getByText("Body measurement", { exact: true }).click();
    await expect
      .element(page.getByText("Not yet supported by Nocturne"))
      .toBeVisible();
    await expect.element(page.getByText("Step history")).toBeVisible();
  });

  it("keeps catalog categories visible when inventory is incomplete", async () => {
    googleHealthMocks.status.mockResolvedValue(
      status({
        configured: true,
        connected: true,
        grantedTypes: ["steps", "heart-rate", "weight", "sleep"],
      })
    );
    googleHealthMocks.preview.mockResolvedValue({
      items: [
        { dataType: "steps", granted: true, supported: true, count: 4 },
        { dataType: "sleep", granted: true, supported: true, count: 1 },
      ],
    });
    render(GoogleHealthPage);

    await expect
      .element(page.getByTestId("google-health-category-Vitals"))
      .toBeVisible();
    await expect
      .element(page.getByTestId("google-health-category-Body measurement"))
      .toBeVisible();
    await expect
      .element(page.getByRole("row", { name: /Heart rate/ }))
      .toHaveTextContent("Not scanned");
    await expect
      .element(page.getByRole("checkbox", { name: "Import Heart rate" }))
      .toBeEnabled();
  });

  it("keeps catalog categories visible when the inventory scan is already running", async () => {
    googleHealthMocks.status.mockResolvedValue(
      status({
        configured: true,
        connected: true,
        grantedTypes: ["steps", "heart-rate", "weight", "sleep"],
      })
    );
    googleHealthMocks.preview.mockRejectedValue({
      status: 409,
      body: { message: "already_running" },
    });
    render(GoogleHealthPage);

    await expect
      .element(page.getByTestId("google-health-category-Vitals"))
      .toBeVisible();
    await expect
      .element(page.getByTestId("google-health-category-Body measurement"))
      .toBeVisible();
    await expect
      .element(page.getByRole("row", { name: /Heart rate/ }))
      .toHaveTextContent("Not scanned");
  });

  it.each(["already_running", "google_unavailable"])(
    "clears stale inventory feedback after retrying %s",
    async (errorCode) => {
      googleHealthMocks.status.mockResolvedValue(
        status({ configured: true, connected: true })
      );
      googleHealthMocks.preview
        .mockRejectedValueOnce({
          status: errorCode === "already_running" ? 409 : 502,
          body: { message: errorCode },
        })
        .mockResolvedValue({
          items: [
            {
              dataType: "heart-rate",
              granted: true,
              supported: true,
              count: 42,
            },
          ],
        });
      render(GoogleHealthPage);
      const feedback = page.getByRole(
        errorCode === "already_running" ? "status" : "alert"
      );
      await expect.element(feedback).toBeVisible();
      await page
        .getByRole("button", { name: "Refresh inventory", exact: true })
        .click();
      await expect
        .element(page.getByRole("row", { name: /Heart rate/ }))
        .toHaveTextContent("42");
      await expect.element(feedback).not.toBeInTheDocument();
    }
  );

  it("allows problematic selected types to be unchecked and saved without syncing", async () => {
    googleHealthMocks.status.mockResolvedValue(
      status({
        configured: true,
        connected: true,
        selectedTypes: ["steps", "heart-rate", "weight"],
      })
    );
    googleHealthMocks.preview.mockResolvedValue({
      items: [
        { dataType: "steps", granted: true, supported: true, count: 4 },
        { dataType: "heart-rate", granted: true, supported: true, count: 0 },
        {
          dataType: "weight",
          granted: true,
          supported: true,
          count: 0,
          errorCode: "google_unavailable",
        },
      ],
    });
    render(GoogleHealthPage);
    await page.getByRole("checkbox", { name: "Import Heart rate" }).click();
    await page.getByText("Body measurement", { exact: true }).click();
    await page.getByRole("checkbox", { name: "Import Weight" }).click();
    await page
      .getByRole("button", { name: "Save import settings", exact: true })
      .click();
    await expect.poll(() => googleHealthMocks.save.mock.calls.length).toBe(1);
    expect(googleHealthMocks.save).toHaveBeenCalledWith(
      expect.objectContaining({ dataTypes: ["steps"] })
    );
    expect(googleHealthMocks.sync).not.toHaveBeenCalled();
    expect(googleHealthMocks.disconnect).not.toHaveBeenCalled();
  });

  it("preserves a consumed import date when saving other settings", async () => {
    googleHealthMocks.status.mockResolvedValue(
      status({
        configured: true,
        connected: true,
        importFrom: undefined,
        backfillComplete: true,
      })
    );
    render(GoogleHealthPage);
    await expect
      .element(
        page.getByRole("button", { name: "Save import settings", exact: true })
      )
      .toBeEnabled();
    await page
      .getByRole("button", { name: "Save import settings", exact: true })
      .click();
    await expect.poll(() => googleHealthMocks.save.mock.calls.length).toBe(1);
    expect(googleHealthMocks.save).toHaveBeenCalledWith(
      expect.objectContaining({ importFrom: null })
    );
  });

  it("allows reconnecting after the backfill consumes the import date", async () => {
    googleHealthMocks.status.mockResolvedValue(
      status({
        configured: true,
        connected: false,
        clientId: "test-client.apps.googleusercontent.com",
        callbackUrl:
          "https://nocturne.test/settings/connectors/google-health/callback",
        importFrom: undefined,
        backfillComplete: true,
      })
    );
    googleHealthMocks.start.mockResolvedValue({ url: "" });
    render(GoogleHealthPage);

    await page.getByText("Edit connection settings", { exact: true }).click();
    await expect
      .element(page.getByLabelText("Import data from"))
      .toHaveValue("");
    await page.getByRole("button", { name: "Save and reconnect" }).click();

    await expect.poll(() => googleHealthMocks.save.mock.calls.length).toBe(1);
    expect(googleHealthMocks.save).toHaveBeenCalledWith(
      expect.objectContaining({ importFrom: null })
    );
    expect(googleHealthMocks.start).toHaveBeenCalledTimes(1);
  });

  it("saves an older history date and an empty selection without reconnecting", async () => {
    googleHealthMocks.status
      .mockResolvedValueOnce(
        status({
          configured: true,
          connected: true,
          selectedTypes: ["heart-rate"],
          importFrom: "2026-08-29T00:00:00Z",
        })
      )
      .mockResolvedValue(
        status({
          configured: true,
          connected: true,
          selectedTypes: [],
          importFrom: "2020-01-01T00:00:00Z",
        })
      );
    googleHealthMocks.preview.mockResolvedValue({
      items: [
        { dataType: "heart-rate", granted: true, supported: true, count: 0 },
      ],
    });
    render(GoogleHealthPage);
    await page.getByRole("checkbox", { name: "Import Heart rate" }).click();
    await page.getByLabelText("Import data from").fill("2020-01-01");
    await page
      .getByRole("button", { name: "Save import settings", exact: true })
      .click();
    await expect.poll(() => googleHealthMocks.save.mock.calls.length).toBe(1);
    expect(googleHealthMocks.save).toHaveBeenCalledWith(
      expect.objectContaining({
        dataTypes: [],
        importFrom: "2020-01-01T00:00:00.000Z",
        clientSecret: null,
      })
    );
    expect(googleHealthMocks.sync).not.toHaveBeenCalled();
    expect(googleHealthMocks.start).not.toHaveBeenCalled();
    expect(googleHealthMocks.disconnect).not.toHaveBeenCalled();
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

  it("allows a supported empty type to be enabled for future measurements", async () => {
    googleHealthMocks.status.mockResolvedValue(
      status({ configured: true, connected: true, selectedTypes: ["steps"] })
    );
    googleHealthMocks.preview.mockResolvedValue({
      items: [
        { dataType: "heart-rate", granted: true, supported: true, count: 0 },
      ],
    });
    render(GoogleHealthPage);
    await page.getByRole("checkbox", { name: "Import Heart rate" }).click();
    await expect
      .element(page.getByRole("checkbox", { name: "Import Heart rate" }))
      .toBeChecked();
  });

  it("requires preview confirmation before claiming that data is importing", async () => {
    googleHealthMocks.status.mockResolvedValue(
      status({ configured: true, connected: true, previewRequired: true })
    );
    googleHealthMocks.preview.mockResolvedValue({
      items: [{ dataType: "steps", granted: true, count: 42, supported: true }],
    });
    render(GoogleHealthPage);

    await expect
      .element(
        page.getByText("Review the available data below", { exact: false })
      )
      .toBeVisible();
    await expect
      .element(page.getByText("Available to connect", { exact: true }))
      .toBeVisible();
    await expect
      .element(page.getByRole("button", { name: "Sync now" }))
      .toBeDisabled();
    await expect
      .element(page.getByRole("button", { name: "Save selection and import" }))
      .toBeEnabled();
    expect(googleHealthMocks.sync).not.toHaveBeenCalled();
  });

  it("keeps import status tied to saved selection while checkboxes are edited", async () => {
    googleHealthMocks.status.mockResolvedValue(
      status({ configured: true, connected: true, selectedTypes: ["steps"] })
    );
    googleHealthMocks.preview.mockResolvedValue({
      items: [
        { dataType: "steps", granted: true, count: 42, supported: true },
        { dataType: "weight", granted: true, count: 3, supported: true },
      ],
    });
    render(GoogleHealthPage);
    const steps = page.getByRole("row", { name: /Steps/ });
    const weight = page.getByRole("row", { name: /Weight/ });

    await expect
      .element(steps.getByText("Import enabled", { exact: true }))
      .toBeVisible();
    await expect
      .element(weight.getByText("Available to connect", { exact: true }))
      .toBeVisible();
    await page.getByRole("checkbox", { name: "Import Steps" }).click();
    await page.getByRole("checkbox", { name: "Import Weight" }).click();

    await expect
      .element(steps.getByText("Import enabled", { exact: true }))
      .toBeVisible();
    await expect
      .element(weight.getByText("Available to connect", { exact: true }))
      .toBeVisible();
    expect(googleHealthMocks.save).not.toHaveBeenCalled();
  });

  it("shows an import error for the affected saved data type", async () => {
    googleHealthMocks.status.mockResolvedValue(
      status({
        configured: true,
        connected: true,
        errorCode: "internal_sync_native_write",
        errorDataTypes: ["steps"],
      })
    );
    googleHealthMocks.preview.mockResolvedValue({
      items: [
        { dataType: "steps", granted: true, count: 42, supported: true },
        { dataType: "weight", granted: true, count: 3, supported: true },
      ],
    });
    render(GoogleHealthPage);

    await expect
      .element(page.getByText("Import needs attention", { exact: true }))
      .toBeVisible();
    await expect
      .element(page.getByText("Import enabled", { exact: true }))
      .toBeVisible();
  });

  it("shows server-side progress while a large import continues in the background", async () => {
    googleHealthMocks.status.mockResolvedValue(
      status({
        configured: true,
        connected: true,
        isSyncing: true,
        syncPhase: GoogleHealthSyncPhase.Reading,
        syncDataType: "steps",
        syncCompletedDataTypes: 1,
        syncTotalDataTypes: 4,
        syncPagesRead: 12,
        syncProgressPercent: 22,
      })
    );
    render(GoogleHealthPage);

    await expect
      .element(page.getByText("Import running in the background"))
      .toBeVisible();
    await expect.element(page.getByText("Reading Steps")).toBeVisible();
    await expect
      .element(page.getByText("12 Google pages read for this data type"))
      .toBeVisible();
    await expect
      .element(
        page.getByRole("progressbar", { name: "Google Health import progress" })
      )
      .toHaveAttribute("aria-valuenow", "22");
    await expect
      .element(page.getByRole("button", { name: "Sync now" }))
      .toBeDisabled();
    expect(googleHealthMocks.preview).not.toHaveBeenCalled();
  });

  it("shows persisted historical progress after reloading", async () => {
    googleHealthMocks.status.mockResolvedValue(
      status({
        configured: true,
        connected: true,
        backfillSyncedThrough: "2025-06-01T00:00:00.000Z",
        backfillComplete: false,
      })
    );
    render(GoogleHealthPage);

    await expect
      .element(page.getByText("Historical import progress", { exact: true }))
      .toBeVisible();
    await expect
      .element(
        page.getByText("Synchronized back through 2025-06-01.", {
          exact: false,
        })
      )
      .toBeVisible();
    await expect
      .element(
        page.getByText(
          "Each sync refreshes today and imports one older calendar month.",
          { exact: false }
        )
      )
      .toBeVisible();
  });

  it("exposes the shared connector reset page for configured imports", async () => {
    googleHealthMocks.status.mockResolvedValue(
      status({ configured: true, connected: true })
    );
    googleHealthMocks.preview.mockResolvedValue({ items: [] });
    render(GoogleHealthPage);

    await expect
      .element(page.getByText("Import recovery", { exact: true }))
      .toBeVisible();
    await expect
      .element(page.getByRole("link", { name: "Open connector reset" }))
      .toHaveAttribute("href", "/settings/admin/connector-cursors");
  });
});
