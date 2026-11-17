import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import { reactiveFields } from "#lib/test-stubs/reactive-fields.svelte.js";
import { relativeAge, time } from "#lib/utils/formatting.js";

const store = reactiveFields({
  connectionPresentation: "live" as
    | "live"
    | "pending"
    | "denied"
    | "unavailable",
  connectionError: null as { message: string; timestamp: number } | null,
  connectionStats: { messageCount: 7, reconnectCount: 2 },
  timeSinceUpdate: 0,
  lastUpdated: 0,
  now: 0,
  reconnect: vi.fn(),
});

vi.mock("#lib/stores/realtime-store.svelte.js", () => ({
  getRealtimeStore: () => store,
}));

import ConnectionStatus from "./ConnectionStatus.svelte";
import WebSocketStatus from "./WebSocketStatus.svelte";
import ConnectionStatusWidget from "./dashboard/widgets/ConnectionStatusWidget.svelte";

describe("ConnectionStatus", () => {
  it("shows the live state with message count and never-updated text", async () => {
    store.connectionPresentation = "live";
    render(ConnectionStatus);
    await expect.element(page.getByText("Connected")).toBeVisible();
    await expect.element(page.getByText("7")).toBeVisible();
    await expect.element(page.getByText("Never")).toBeVisible();
  });

  it("shows the reconnect count only when detailed", async () => {
    store.connectionPresentation = "live";
    const plain = render(ConnectionStatus);
    await expect.element(page.getByText("Messages:")).toBeVisible();
    expect(page.getByText("Reconnects:").elements()).toHaveLength(0);
    plain.unmount();

    render(ConnectionStatus, { detailed: true });
    await expect.element(page.getByText("Reconnects:")).toBeVisible();
  });

  it("offers a retry that reconnects when unavailable", async () => {
    store.connectionPresentation = "unavailable";
    store.connectionError = { message: "Socket closed", timestamp: 0 };
    render(ConnectionStatus);
    await expect.element(page.getByText("Disconnected")).toBeVisible();
    await expect
      .element(page.getByText("Error:", { exact: true }))
      .toBeVisible();
    await page.getByRole("button", { name: "Retry" }).click();
    expect(store.reconnect).toHaveBeenCalledOnce();
  });

  it("labels the error as a connection error when detailed", async () => {
    store.connectionPresentation = "unavailable";
    store.connectionError = { message: "Socket closed", timestamp: 0 };
    render(ConnectionStatus, { detailed: true });
    await expect.element(page.getByText("Connection error:")).toBeVisible();
  });

  it("shows no retry while pending", async () => {
    store.connectionPresentation = "pending";
    render(ConnectionStatus);
    await expect.element(page.getByText("Connecting...")).toBeVisible();
    expect(page.getByRole("button", { name: "Retry" }).elements()).toHaveLength(
      0
    );
  });

  it("shows the age of the last update while live", async () => {
    const now = Date.UTC(2026, 0, 1, 12, 0, 0);
    const lastUpdated = now - 5 * 60000;
    store.connectionPresentation = "live";
    store.timeSinceUpdate = 5 * 60000;
    store.lastUpdated = lastUpdated;
    store.now = now;
    render(ConnectionStatus);
    await expect
      .element(page.getByText(relativeAge(lastUpdated, { now })))
      .toBeVisible();
    expect(page.getByText("Never").elements()).toHaveLength(0);

    store.now = now + 60 * 60000;
    await expect
      .element(page.getByText(relativeAge(lastUpdated, { now: store.now })))
      .toBeVisible();
  });

  it("shows the error time only when detailed", async () => {
    const timestamp = Date.UTC(2026, 0, 1, 12, 34, 0);
    store.connectionPresentation = "unavailable";
    store.connectionError = { message: "Socket closed", timestamp };
    const plain = render(ConnectionStatus);
    await expect
      .element(page.getByText("Error:", { exact: true }))
      .toBeVisible();
    expect(
      page.getByText(time(new Date(timestamp)), { exact: true }).elements()
    ).toHaveLength(0);
    plain.unmount();

    render(ConnectionStatus, { detailed: true });
    await expect
      .element(page.getByText(time(new Date(timestamp)), { exact: true }))
      .toBeVisible();
  });

  it("shows the denied state without a retry", async () => {
    store.connectionPresentation = "denied";
    render(ConnectionStatus);
    await expect.element(page.getByText("Not available")).toBeVisible();
    await expect
      .element(page.getByText("Live updates are not permitted for this view"))
      .toBeVisible();
    expect(page.getByRole("button", { name: "Retry" }).elements()).toHaveLength(
      0
    );
  });

  describe("placements", () => {
    it("the connection card is detailed", async () => {
      store.connectionPresentation = "unavailable";
      store.connectionError = { message: "Socket closed", timestamp: 0 };
      render(WebSocketStatus);
      await expect.element(page.getByText("Connection error:")).toBeVisible();
    });

    it("the dashboard widget is not detailed", async () => {
      store.connectionPresentation = "unavailable";
      store.connectionError = { message: "Socket closed", timestamp: 0 };
      render(ConnectionStatusWidget);
      await expect.element(page.getByText("Connection")).toBeVisible();
      await expect
        .element(page.getByText("Error:", { exact: true }))
        .toBeVisible();
      expect(page.getByText("Connection error:").elements()).toHaveLength(0);
    });
  });
});
