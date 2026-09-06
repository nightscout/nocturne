import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import type { ConnectorStatusDto } from "$lib/api/generated/nocturne-api-client";
import FirstReadingEmptyState from "./FirstReadingEmptyState.svelte";

function connector(
  overrides: Partial<ConnectorStatusDto> = {}
): ConnectorStatusDto {
  return {
    id: "dexcom",
    name: "Dexcom Share",
    hasDatabaseConfig: true,
    isEnabled: true,
    totalEntries: 0,
    ...overrides,
  };
}

describe("FirstReadingEmptyState", () => {
  it("offers the three no-connector paths when nothing is configured", async () => {
    render(FirstReadingEmptyState, { connectors: [] });

    await expect
      .element(page.getByTestId("path-connector"))
      .toHaveAttribute("href", "/settings/connectors");
    await expect
      .element(page.getByTestId("path-uploader"))
      .toHaveAttribute("href", "/settings/connectors#api-tokens-section");
    await expect
      .element(page.getByTestId("path-migration"))
      .toHaveAttribute("href", "/settings/migration");
  });

  it("names the connector and links to the connector page when one is configured", async () => {
    render(FirstReadingEmptyState, { connectors: [connector()] });

    await expect
      .element(page.getByText("Waiting for the first sync from Dexcom Share"))
      .toBeVisible();
    // The no-connector paths must not appear when a connector is waiting.
    await expect
      .element(page.getByTestId("path-migration"))
      .not.toBeInTheDocument();
  });

  it("says a sync has not run yet when there is no sync attempt", async () => {
    render(FirstReadingEmptyState, {
      connectors: [
        connector({
          lastSyncAttempt: undefined,
          lastSuccessfulSync: undefined,
        }),
      ],
    });

    await expect
      .element(page.getByTestId("connector-not-synced"))
      .toBeVisible();
    await expect
      .element(page.getByTestId("connector-synced-empty"))
      .not.toBeInTheDocument();
  });

  it("distinguishes a completed sync that fetched zero records", async () => {
    render(FirstReadingEmptyState, {
      connectors: [
        connector({
          lastSuccessfulSync: new Date(),
          lastSyncAttempt: new Date(),
          totalEntries: 0,
        }),
      ],
    });

    await expect
      .element(page.getByTestId("connector-synced-empty"))
      .toBeVisible();
    await expect
      .element(page.getByTestId("connector-not-synced"))
      .not.toBeInTheDocument();
  });

  it("surfaces the last attempt's outcome when the connector reports one", async () => {
    render(FirstReadingEmptyState, {
      connectors: [
        connector({
          lastSyncAttempt: new Date(),
          stateMessage: "Authentication failed",
        }),
      ],
    });

    await expect
      .element(page.getByTestId("connector-outcome"))
      .toHaveTextContent("Authentication failed");
  });
});
