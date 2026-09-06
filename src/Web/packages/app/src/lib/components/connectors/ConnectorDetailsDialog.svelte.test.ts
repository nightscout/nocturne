import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { error } from "@sveltejs/kit";
import { ApiException, type SyncResult } from "$api-clients";
import { SyncRequestSchema } from "$lib/api/generated/schemas";

let syncImpl: () => Promise<SyncResult>;
let sentRequest: unknown;

vi.mock("$api/generated/services.generated.remote", () => ({
  triggerConnectorSync: (payload: { request: unknown }) => {
    sentRequest = payload.request;
    return syncImpl();
  },
}));

// The dialog uses Tooltip.Root, whose provider the app layout supplies; the wrapper stands in for it.
import ConnectorDetailsDialog from "./connector-details-dialog-test-wrapper.svelte";

const FALLBACK = "We couldn't start the sync. Please try again.";

async function syncing(
  impl: () => Promise<SyncResult>,
  onSyncComplete?: () => Promise<void>
) {
  syncImpl = impl;

  render(ConnectorDetailsDialog, {
    props: {
      open: true,
      onSyncComplete,
      selectedConnector: {
        id: "nightscout",
        name: "Nightscout",
        status: "Active",
        state: "Configured",
        isHealthy: true,
      },
      selectedConnectorCapabilities: {
        supportsManualSync: true,
        supportsHistoricalSync: true,
      },
    },
  });

  await page.getByRole("button", { name: "Sync Now" }).click();
}

const syncReturning = (result: SyncResult) => syncing(async () => result);

describe("ConnectorDetailsDialog", () => {
  beforeEach(() => {
    sentRequest = undefined;
  });

  it("reports the count a failed sync still landed", async () => {
    await syncReturning({
      success: false,
      message: "Failed to sync Notes: the source refused the request",
      errors: ["Failed to sync Notes: the source refused the request"],
      itemsSynced: { Glucose: 288, Boluses: 12 },
    });

    await expect
      .element(
        page.getByText("Failed to sync Notes: the source refused the request")
      )
      .toBeVisible();
    await expect.element(page.getByText("(300 items)")).toBeVisible();
  });

  it("reports a zero count for a failed sync that landed nothing", async () => {
    await syncReturning({
      success: false,
      message: "Sync failed while fetching data",
      errors: ["Failed to fetch Glucose"],
      itemsSynced: { Glucose: 0 },
    });

    await expect.element(page.getByText("(0 items)")).toBeVisible();
  });

  it("reports the count a successful sync landed", async () => {
    await syncReturning({
      success: true,
      message: "",
      errors: [],
      itemsSynced: { Glucose: 288 },
    });

    await expect.element(page.getByText("(288 items)")).toBeVisible();
  });

  it("shows the server's reason when the sync was refused", async () => {
    await syncing(async () => error(409, "A sync for this connector is already running."));

    await expect
      .element(page.getByText("A sync for this connector is already running."))
      .toBeInTheDocument();
  });

  it("keeps a server fault behind the dialog's own wording", async () => {
    await syncing(async () => error(500, "npgsql: connection reset"));

    await expect.element(page.getByText(FALLBACK)).toBeInTheDocument();
    expect(page.getByText("npgsql: connection reset").elements()).toHaveLength(
      0
    );
  });

  /**
   * The generated client's own rejection is an `Error`, so rendering a caught
   * `message` reads as safe and is not: it is boilerplate the client wrote,
   * and a body it could not parse arrives as a `SyntaxError` quoting the body.
   */
  it("keeps the generated client's own error text out of the panel", async () => {
    await syncing(async () => {
      throw new ApiException(
        "An unexpected server error occurred.",
        500,
        "<html><body>502 Bad Gateway</body></html>",
        {},
        null
      );
    });

    await expect.element(page.getByText(FALLBACK)).toBeInTheDocument();
    expect(
      page.getByText("An unexpected server error occurred.").elements()
    ).toHaveLength(0);
    expect(page.getByText("502 Bad Gateway", { exact: false }).elements()).toHaveLength(0);
  });

  it("keeps an unparsable response body out of the panel", async () => {
    await syncing(async () => {
      JSON.parse("<html><body>502 Bad Gateway</body></html>");
      throw new Error("unreachable");
    });

    await expect.element(page.getByText(FALLBACK)).toBeInTheDocument();
    expect(page.getByText("502 Bad Gateway", { exact: false }).elements()).toHaveLength(0);
  });

  /**
   * The command validates its payload against this schema, which takes a
   * date-time as an ISO string and rejects a `Date`, so a range built the
   * obvious way would fail every sync at that boundary.
   */
  it("sends a range the command's schema accepts", async () => {
    await syncReturning({
      success: true,
      message: "",
      errors: [],
      itemsSynced: { Glucose: 1 },
    });

    const parsed = SyncRequestSchema.safeParse(sentRequest);

    expect(parsed.error?.issues ?? []).toEqual([]);
    expect(sentRequest).toMatchObject({
      from: expect.stringMatching(/^\d{4}-\d{2}-\d{2}T/),
      to: expect.stringMatching(/^\d{4}-\d{2}-\d{2}T/),
    });
  });

  it("keeps a refresh the caller could not finish out of the sync's result", async () => {
    const logged: unknown[] = [];
    vi.spyOn(console, "error").mockImplementation((...args) =>
      logged.push(args.join(" "))
    );

    let calls = 0;
    await syncing(
      async () => ({
        success: true,
        message: "",
        errors: [],
        itemsSynced: { Glucose: 288 },
      }),
      async () => {
        calls += 1;
        throw error(500, "npgsql: connection reset");
      }
    );

    await expect
      .element(page.getByText("Sync initiated successfully"))
      .toBeInTheDocument();
    await expect.element(page.getByText("(288 items)")).toBeVisible();
    expect(page.getByText(FALLBACK).elements()).toHaveLength(0);
    expect(calls).toBe(1);
    expect(logged.join(" ")).toContain("Failed to refresh after a connector sync");

    vi.restoreAllMocks();
  });
});
