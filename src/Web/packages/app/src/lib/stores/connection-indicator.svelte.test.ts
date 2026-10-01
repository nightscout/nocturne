import { describe, it, expect, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import Harness from "./ConnectionIndicatorHarness.test.svelte";
import type { WebSocketConnectionStatus } from "$lib/websocket/types";

/**
 * The debounce itself, as opposed to the classification `isErrorStatus` covers.
 * Every surface that reports "Connection Error" reads this, so a grace window
 * that never latches hides a genuinely dead feed, and one that latches on the
 * ordinary connect churn of a page load is the false alarm the whole fix exists
 * to remove.
 */

const GRACE_MS = 60;

function setup(initial: WebSocketConnectionStatus) {
  let status = $state<WebSocketConnectionStatus>(initial);
  const screen = render(Harness, {
    status: () => status,
    graceMs: GRACE_MS,
  });
  const indicator = screen.getByTestId("indicator");
  return {
    indicator,
    set(next: WebSocketConnectionStatus) {
      status = next;
    },
  };
}

describe("createConnectionIndicator", () => {
  it.each<WebSocketConnectionStatus>([
    "idle",
    "connecting",
    "reconnecting",
    "connected",
    "unauthorized",
  ])("never reports %s, however long it lasts", async (status) => {
    const { indicator } = setup(status);

    await new Promise((resolve) => setTimeout(resolve, GRACE_MS * 3));

    await expect.element(indicator).toHaveTextContent("quiet");
  });

  it("stays quiet through a drop shorter than the grace window", async () => {
    const { indicator, set } = setup("connected");

    set("disconnected");
    await new Promise((resolve) => setTimeout(resolve, GRACE_MS / 3));
    // Read once, without retrying: the claim is that nothing was reported
    // *during* the drop, and a matcher that waits for the text to settle
    // reports a pass either way.
    expect(indicator.element().textContent?.trim()).toBe("quiet");

    set("connected");
    await new Promise((resolve) => setTimeout(resolve, GRACE_MS * 3));
    expect(indicator.element().textContent?.trim()).toBe("quiet");
  });

  it("reports a drop that outlasts the grace window", async () => {
    const { indicator, set } = setup("connected");

    set("disconnected");

    await vi.waitFor(() =>
      expect(indicator.element().textContent?.trim()).toBe("reported")
    );
  });

  it("clears the report as soon as the socket recovers", async () => {
    const { indicator, set } = setup("connected");

    set("error");
    await vi.waitFor(() =>
      expect(indicator.element().textContent?.trim()).toBe("reported")
    );

    set("connected");
    await expect.element(indicator).toHaveTextContent("quiet");
  });
});
