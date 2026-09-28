import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";

const { goto } = vi.hoisted(() => ({ goto: vi.fn(() => Promise.resolve()) }));

vi.mock("$app/navigation", () => ({ goto }));

vi.mock("$lib/stores/realtime-store.svelte", () => ({
  getRealtimeStore: () => ({ pillsData: {} }),
}));

// The vitals strip reads live glucose; the palette under test only owns its items.
vi.mock("./CommandPaletteVitals.svelte", () => ({ default: () => {} }));

import CommandPalette from "./CommandPalette.svelte";
import { pinnedItemIds, recentItemIds } from "./command-palette-store.svelte";

const item = (name: string) => page.getByRole("option", { name, exact: true });

describe("CommandPalette", () => {
  beforeEach(() => {
    goto.mockClear();
    pinnedItemIds.current = [];
    recentItemIds.current = [];
    pageState.data = { effectivePermissions: [], isPlatformAdmin: false };
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

  it("offers Audit to a viewer holding audit.read", async () => {
    pageState.data = { effectivePermissions: ["audit.read"] };
    render(CommandPalette, { open: true });

    await expect.element(item("Audit")).toBeVisible();
  });

  it("offers Audit to a viewer holding audit.manage", async () => {
    pageState.data = { effectivePermissions: ["audit.manage"] };
    render(CommandPalette, { open: true });

    await expect.element(item("Audit")).toBeVisible();
  });

  it("withholds Audit from a viewer holding neither audit scope", async () => {
    pageState.data = { effectivePermissions: ["glucose.read"] };
    render(CommandPalette, { open: true });

    await expect.element(item("Dashboard")).toBeVisible();
    await expect.element(item("Audit")).not.toBeInTheDocument();
  });

  it("offers Grants to a viewer holding sharing.manage", async () => {
    pageState.data = { effectivePermissions: ["sharing.manage"] };
    render(CommandPalette, { open: true });

    await expect.element(item("Grants")).toBeVisible();
  });

  it("withholds Grants from a viewer without sharing.manage", async () => {
    pageState.data = { effectivePermissions: ["glucose.read"] };
    render(CommandPalette, { open: true });

    await expect.element(item("Dashboard")).toBeVisible();
    await expect.element(item("Grants")).not.toBeInTheDocument();
  });

  it("offers the platform admin pages to a platform administrator", async () => {
    pageState.data = { effectivePermissions: [], isPlatformAdmin: true };
    render(CommandPalette, { open: true });

    await expect.element(item("Tenant Management")).toBeVisible();
    await expect.element(item("Access Requests")).toBeVisible();
  });

  it("withholds the platform admin pages from everyone else", async () => {
    pageState.data = { effectivePermissions: ["*"], isPlatformAdmin: false };
    render(CommandPalette, { open: true });

    await expect.element(item("Dashboard")).toBeVisible();
    await expect.element(item("Tenant Management")).not.toBeInTheDocument();
    await expect.element(item("Access Requests")).not.toBeInTheDocument();
  });
});
