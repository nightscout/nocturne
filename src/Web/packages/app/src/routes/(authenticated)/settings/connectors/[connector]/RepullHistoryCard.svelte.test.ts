import { render } from "vitest-browser-svelte";
import { page as browser } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { page } from "$app/state";
import type { SyncResult } from "$api-clients";

let sentPayload: unknown;
let resetImpl: () => Promise<SyncResult>;
let supportedDataTypes: string[] | undefined;

vi.mock("$api/generated/services.generated.remote", () => ({
  getConnectorCapabilities: () => ({
    current: { supportedDataTypes, supportsHistoricalSync: true },
  }),
  resetConnectorCursor: (payload: unknown) => {
    sentPayload = payload;
    return resetImpl();
  },
}));

import RepullHistoryCard from "./RepullHistoryCard.svelte";

const ok: SyncResult = {
  success: true,
  message: "Done",
  itemsSynced: { Glucose: 412 },
  errors: [],
};

function renderCard() {
  return render(RepullHistoryCard, { props: { connectorId: "nightscout" } });
}

describe("RepullHistoryCard", () => {
  beforeEach(() => {
    sentPayload = undefined;
    resetImpl = async () => ok;
    supportedDataTypes = ["Glucose", "Boluses"];
    page.data = { effectivePermissions: ["admin"] };
  });

  it("offers nothing to someone the API would refuse", async () => {
    // reset-cursor is [RequireAdmin]; an ordinary member opening their connector's page would
    // otherwise be handed a button whose only outcome is a 403.
    page.data = { effectivePermissions: ["glucose.read"] };
    renderCard();

    await expect
      .element(browser.getByTestId("repull-history"))
      .not.toBeInTheDocument();
  });

  it("offers the control to an admin", async () => {
    renderCard();

    await expect.element(browser.getByTestId("repull-history")).toBeVisible();
  });

  it("reads a wildcard grant as admin", async () => {
    page.data = { effectivePermissions: ["*"] };
    renderCard();

    await expect.element(browser.getByTestId("repull-history")).toBeVisible();
  });

  it("holds the re-pull back until a start date is chosen", async () => {
    // An unbounded re-pull is the one shape the synchronous endpoint cannot be trusted with,
    // so the date is the gate rather than a default.
    renderCard();

    await expect
      .element(browser.getByTestId("repull-history-start"))
      .toBeDisabled();
  });

  it("sends the chosen date and only the kinds of data still ticked", async () => {
    renderCard();

    await browser.getByLabelText("Download from").fill("2026-07-01");
    await browser.getByTestId("repull-type-Boluses").click();
    await browser.getByTestId("repull-history-start").click();
    await browser
      .getByRole("button", { name: "Download history" })
      .last()
      .click();

    await vi.waitFor(() => expect(sentPayload).toBeDefined());
    expect(sentPayload).toEqual({
      id: "nightscout",
      request: {
        from: "2026-07-01T00:00:00.000Z",
        dataTypes: ["Glucose"],
      },
    });
  });

  it("reports what the re-pull brought back", async () => {
    renderCard();

    await browser.getByLabelText("Download from").fill("2026-07-01");
    await browser.getByTestId("repull-history-start").click();
    await browser
      .getByRole("button", { name: "Download history" })
      .last()
      .click();

    await expect.element(browser.getByText("Glucose: 412")).toBeVisible();
  });

  it("offers every data type when the connector declares none", async () => {
    supportedDataTypes = undefined;
    renderCard();

    await expect
      .element(browser.getByTestId("repull-type-DeviceStatus"))
      .toBeVisible();
  });

  it("says the work may outlive the request when it fails", async () => {
    // A timed-out gateway looks exactly like a refusal from here. Telling someone to retry a
    // multi-minute re-pull that is still running is the worse of the two readings.
    resetImpl = async () => {
      throw new Error("gateway timeout");
    };
    renderCard();

    await browser.getByLabelText("Download from").fill("2026-07-01");
    await browser.getByTestId("repull-history-start").click();
    await browser
      .getByRole("button", { name: "Download history" })
      .last()
      .click();

    const alert = browser.getByTestId("repull-error");
    await expect.element(alert).toBeVisible();
    expect(alert.element().textContent?.replace(/\s+/g, " ")).toContain(
      "it may still be finishing on the server"
    );
  });
});
