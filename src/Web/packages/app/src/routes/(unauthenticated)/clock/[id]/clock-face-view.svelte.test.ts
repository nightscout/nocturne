import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page as browserPage, userEvent } from "vitest/browser";
import { page } from "$app/state";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";

const navigation = vi.hoisted(() => ({ goto: vi.fn(async () => {}) }));
vi.mock("$app/navigation", () => navigation);
vi.mock("#lib/stores/public-clock-store.svelte.js", () => ({
  PublicClockStore: class {
    demoMode = false;
    watchAlerts() {}
    start() {}
    stop() {}
  },
}));
vi.mock("#lib/stores/realtime-store.svelte.js", () => ({ setClockGlucoseSource: () => {} }));
vi.mock("#lib/components/clock/ClockFaceRenderer.svelte", () => ({ default: () => {} }));
vi.mock("#lib/components/clock/ClockAlertBand.svelte", () => ({ default: () => {} }));
vi.mock("#lib/api/generated/clockFaces.generated.remote.js", () => ({
  getById: () => remoteQuery(() => ({ id: "f", config: { rows: [], settings: {} } })),
}));

import ClockFaceView from "./+page@.svelte";

describe("clock face view", () => {
  beforeEach(() => {
    page.params.id = "11111111-1111-1111-1111-111111111111";
    navigation.goto.mockClear();
  });

  const tap = (target: Element) => {
    target.dispatchEvent(new PointerEvent("pointerdown", { pointerType: "touch", bubbles: true }));
    target.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true }));
  };

  it("offers Edit to a caller holding sharing.manage", async () => {
    page.data.effectivePermissions = ["sharing.manage"];
    render(ClockFaceView);
    await expect.element(browserPage.getByRole("button", { name: "Edit" })).toBeInTheDocument();
  });

  it("withholds Edit from a caller without it", async () => {
    page.data.effectivePermissions = ["glucose.read"];
    render(ClockFaceView);
    await expect.element(browserPage.getByRole("button", { name: "Back" })).toBeInTheDocument();
    expect(browserPage.getByRole("button", { name: "Edit" }).query()).toBeNull();
  });

  it("shows the bar on a tap and hides it, untappable, a few seconds later", async () => {
    page.data.effectivePermissions = ["glucose.read"];
    render(ClockFaceView);
    const nav = browserPage.getByTestId("clock-nav");
    await expect.element(nav).toHaveAttribute("data-revealed", "false");

    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    try {
      document.body.dispatchEvent(
        new PointerEvent("pointerdown", { pointerType: "touch", bubbles: true })
      );
      await expect.element(nav).toHaveAttribute("data-revealed", "true");
      expect(nav.element().hasAttribute("inert")).toBe(false);

      await vi.advanceTimersByTimeAsync(5000);
      await expect.element(nav).toHaveAttribute("data-revealed", "false");
      expect(nav.element().hasAttribute("inert")).toBe(true);
    } finally {
      vi.useRealTimers();
    }
  });

  it("takes the tap that shows the hidden bar for itself, not for the button under it", async () => {
    page.data.effectivePermissions = ["glucose.read"];
    render(ClockFaceView);
    const nav = browserPage.getByTestId("clock-nav");
    const back = browserPage.getByRole("button", { name: "Back" });
    await expect.element(nav).toHaveAttribute("data-revealed", "false");

    tap(back.element());
    await expect.element(nav).toHaveAttribute("data-revealed", "true");
    expect(navigation.goto).not.toHaveBeenCalled();

    tap(back.element());
    expect(navigation.goto).toHaveBeenCalledTimes(1);
  });

  it("gives the hidden bar back to the keyboard after a tap", async () => {
    page.data.effectivePermissions = ["glucose.read"];
    render(ClockFaceView);
    const nav = browserPage.getByTestId("clock-nav");
    await expect.element(nav).toHaveAttribute("data-revealed", "false");

    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    try {
      document.body.dispatchEvent(
        new PointerEvent("pointerdown", { pointerType: "touch", bubbles: true })
      );
      await vi.advanceTimersByTimeAsync(5000);
      await expect.element(nav).toHaveAttribute("data-revealed", "false");
      expect(nav.element().hasAttribute("inert")).toBe(true);
    } finally {
      vi.useRealTimers();
    }

    await userEvent.keyboard("{Tab}");
    await expect.element(browserPage.getByRole("button", { name: "Back" })).toHaveFocus();
    expect(nav.element().hasAttribute("inert")).toBe(false);
    await vi.waitFor(() => expect(getComputedStyle(nav.element()).opacity).toBe("1"));
  });
});
