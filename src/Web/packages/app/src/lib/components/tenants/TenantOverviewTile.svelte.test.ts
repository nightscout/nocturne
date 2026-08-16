import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import {
  GlucoseStatus,
  GlucoseDirection,
  AlertRuleSeverity,
  type TenantOverviewItem,
} from "$lib/api/generated/nocturne-api-client";
import TenantOverviewTile from "./TenantOverviewTile.svelte";

function makeTenant(
  overrides: Partial<TenantOverviewItem> = {}
): TenantOverviewItem {
  return {
    tenantId: "00000000-0000-0000-0000-000000000001",
    slug: "alice",
    displayName: "Alice",
    lastReadingAt: new Date(Date.now() - 5 * 60_000),
    latest: {
      mgdl: 120,
      delta: 4,
      direction: GlucoseDirection.Flat,
      trendRate: 0.5,
      timestamp: new Date(Date.now() - 5 * 60_000),
    },
    status: GlucoseStatus.InRange,
    thresholds: { urgentLow: 54, low: 70, high: 180, urgentHigh: 250 },
    activeAlertCount: 0,
    highestActiveSeverity: undefined,
    ...overrides,
  };
}

describe("TenantOverviewTile", () => {
  it("renders name, slug, value and delta", async () => {
    render(TenantOverviewTile, {
      props: { tenant: makeTenant(), baseDomain: "example.com" },
    });

    await expect
      .element(page.getByText("Alice", { exact: true }))
      .toBeVisible();
    await expect
      .element(page.getByText("alice", { exact: true }))
      .toBeVisible();
    await expect.element(page.getByTestId("bg-value")).toHaveTextContent("120");
    await expect.element(page.getByTestId("bg-delta")).toHaveTextContent("+4");
  });

  it("links the tile to the tenant subdomain", async () => {
    render(TenantOverviewTile, {
      props: { tenant: makeTenant(), baseDomain: "example.com" },
    });

    await expect
      .element(page.getByTestId("tenant-tile-link"))
      // Protocol comes from the runner's page, so assert only the host part.
      .toHaveAttribute("href", expect.stringContaining("//alice.example.com"));
  });

  it("colors the value from the server status", async () => {
    render(TenantOverviewTile, {
      props: {
        tenant: makeTenant({ status: GlucoseStatus.UrgentLow }),
        baseDomain: "example.com",
      },
    });

    await expect
      .element(page.getByTestId("bg-value"))
      .toHaveClass(/text-glucose-very-low/);
  });

  it("shows grey styling and freshness text for a stale tenant", async () => {
    render(TenantOverviewTile, {
      props: {
        tenant: makeTenant({
          status: GlucoseStatus.Stale,
          lastReadingAt: new Date(Date.now() - 32 * 60_000),
        }),
        baseDomain: "example.com",
      },
    });

    await expect
      .element(page.getByTestId("bg-value"))
      .toHaveClass(/text-muted-foreground/);
    await expect
      .element(page.getByTestId("freshness"))
      .toHaveTextContent(/Last reading .*32/);
  });

  it("shows an alert badge colored by severity when count > 0", async () => {
    render(TenantOverviewTile, {
      props: {
        tenant: makeTenant({
          activeAlertCount: 3,
          highestActiveSeverity: AlertRuleSeverity.Critical,
        }),
        baseDomain: "example.com",
      },
    });

    const badge = page.getByTestId("alert-badge");
    await expect.element(badge).toHaveTextContent("3");
    await expect.element(badge).toHaveClass(/bg-destructive/);
  });

  it("hides the alert badge when count is 0", async () => {
    render(TenantOverviewTile, {
      props: {
        tenant: makeTenant({ activeAlertCount: 0 }),
        baseDomain: "example.com",
      },
    });

    await expect.element(page.getByTestId("bg-value")).toBeVisible();
    expect(page.getByTestId("alert-badge").elements()).toHaveLength(0);
  });

  it("hides the alert badge when count is null (no alerts permission)", async () => {
    render(TenantOverviewTile, {
      props: {
        tenant: makeTenant({ activeAlertCount: undefined }),
        baseDomain: "example.com",
      },
    });

    await expect.element(page.getByTestId("bg-value")).toBeVisible();
    expect(page.getByTestId("alert-badge").elements()).toHaveLength(0);
  });

  it("renders an em-dash and no-data copy when there is no reading", async () => {
    render(TenantOverviewTile, {
      props: {
        tenant: makeTenant({
          latest: undefined,
          lastReadingAt: undefined,
          status: GlucoseStatus.Unknown,
        }),
        baseDomain: "example.com",
      },
    });

    await expect.element(page.getByTestId("bg-value")).toHaveTextContent("—");
    await expect
      .element(page.getByTestId("freshness"))
      .toHaveTextContent("No recent data");
  });

  it("shows the no-data copy when lastReadingAt is unparseable", async () => {
    render(TenantOverviewTile, {
      props: {
        tenant: makeTenant({
          // eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- NSwag types this Date, but the wire value is an unvalidated string
          lastReadingAt: "not-a-timestamp" as unknown as Date,
        }),
        baseDomain: "example.com",
      },
    });

    await expect
      .element(page.getByTestId("freshness"))
      .toHaveTextContent("No recent data");
  });

  it("renders unlinked when baseDomain is null", async () => {
    render(TenantOverviewTile, {
      props: { tenant: makeTenant(), baseDomain: null },
    });

    await expect.element(page.getByTestId("bg-value")).toBeVisible();
    expect(page.getByTestId("tenant-tile-link").elements()).toHaveLength(0);
  });

  it("falls back to muted styling for an unrecognized status string", async () => {
    render(TenantOverviewTile, {
      props: {
        // eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- simulate a server enum value this client build doesn't know
        tenant: makeTenant({ status: "SomethingNew" as GlucoseStatus }),
        baseDomain: "example.com",
      },
    });

    await expect
      .element(page.getByTestId("bg-value"))
      .toHaveClass(/text-muted-foreground/);
  });

  it("renders an em-dash when latest exists but has no mgdl value", async () => {
    render(TenantOverviewTile, {
      props: {
        tenant: makeTenant({
          latest: { mgdl: undefined, timestamp: new Date() },
        }),
        baseDomain: "example.com",
      },
    });

    await expect.element(page.getByTestId("bg-value")).toHaveTextContent("—");
  });
});
