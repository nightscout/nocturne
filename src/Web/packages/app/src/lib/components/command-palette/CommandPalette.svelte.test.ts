import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { goto } = vi.hoisted(() => ({ goto: vi.fn(() => Promise.resolve()) }));

vi.mock("$app/navigation", () => ({ goto }));

vi.mock("$lib/stores/auth-store.svelte", () => ({
  getAuthStore: () => ({ hasPermission: () => true, hasRole: () => true }),
}));

vi.mock("$lib/stores/realtime-store.svelte", () => ({
  getRealtimeStore: () => ({ pillsData: {} }),
}));

import CommandPalette from "./CommandPalette.svelte";
import { pinnedItemIds, recentItemIds } from "./command-palette-store.svelte";

describe("CommandPalette", () => {
  beforeEach(() => {
    goto.mockClear();
    pinnedItemIds.current = [];
    recentItemIds.current = [];
  });

  it("renders no button inside an option or link", async () => {
    render(CommandPalette, { open: true, tenantless: true });

    // The palette selects its first item a tick after mounting; unmounting before then
    // leaves bits-ui reading a detached ref.
    await expect
      .element(page.getByRole("option").first())
      .toHaveAttribute("aria-selected", "true");

    expect(
      page.getByRole("option").getByRole("button").elements()
    ).toHaveLength(0);
    expect(page.getByRole("link").getByRole("button").elements()).toHaveLength(
      0
    );
  });

  it("pins an item without following its link", async () => {
    // Captured before the pin's own handler runs, so stopping propagation cannot hide it.
    let clickedInsideLink = false;
    const recordClick = (event: MouseEvent) => {
      const target = event.target;
      if (target instanceof Element && target.closest("a"))
        clickedInsideLink = true;
      event.preventDefault();
    };
    window.addEventListener("click", recordClick, true);
    render(CommandPalette, { open: true, tenantless: true });

    await page.getByRole("button", { name: "Pin Appearance" }).click();
    window.removeEventListener("click", recordClick, true);

    await expect
      .element(page.getByRole("button", { name: "Unpin Appearance" }))
      .toBeInTheDocument();
    expect(pinnedItemIds.current).toContain("settings-appearance");
    expect(goto).not.toHaveBeenCalled();
    expect(clickedInsideLink).toBe(false);
  });
});
