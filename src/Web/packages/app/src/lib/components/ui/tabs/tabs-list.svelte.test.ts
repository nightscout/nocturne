import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { afterEach, describe, expect, it } from "vitest";
import TabsListHarness from "./TabsListHarness.test.svelte";

const PHONE = 360;
const LONG_LABELS = ["Voreinstellungen", "Benachrichtigungen", "Konfiguration", "Verlauf", "Definitionen"];

type Box = { left: number; right: number; top: number; bottom: number };

function overlaps(a: Box, b: Box): boolean {
  return a.left < b.right - 0.5 && b.left < a.right - 0.5 && a.top < b.bottom - 0.5 && b.top < a.bottom - 0.5;
}

function tabs(): HTMLElement[] {
  return Array.from(document.querySelectorAll<HTMLElement>("[role='tab']"));
}

function list(): HTMLElement {
  return document.querySelector<HTMLElement>("[data-slot='tabs-list']")!;
}

function rowTops(): number[] {
  return [...new Set(tabs().map((tab) => Math.round(tab.getBoundingClientRect().top)))];
}

function expectEveryTabVisibleAndApart() {
  const listBox = list().getBoundingClientRect();
  const boxes = tabs().map((tab) => tab.getBoundingClientRect());
  for (const [index, tab] of tabs().entries()) {
    const box = boxes[index];
    expect(tab.scrollWidth, tab.textContent ?? "").toBeLessThanOrEqual(tab.clientWidth);
    expect(box.left, tab.textContent ?? "").toBeGreaterThanOrEqual(listBox.left - 0.5);
    expect(box.right, tab.textContent ?? "").toBeLessThanOrEqual(listBox.right + 0.5);
    expect(box.bottom, tab.textContent ?? "").toBeLessThanOrEqual(listBox.bottom + 0.5);
    for (const other of boxes.slice(index + 1)) expect(overlaps(box, other)).toBe(false);
  }
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(window.innerWidth);
}

describe("Tabs.List", () => {
  const original = [window.innerWidth, window.innerHeight] as const;

  afterEach(async () => {
    await page.viewport(original[0], original[1]);
  });

  it("wraps tabs that do not fit into rows without overlap on a phone", async () => {
    await page.viewport(PHONE, 780);
    render(TabsListHarness, { props: { labels: LONG_LABELS, variant: "wrap" } });
    await expect.element(page.getByRole("tab", { name: LONG_LABELS[0] })).toBeVisible();

    expect(rowTops().length).toBeGreaterThan(1);
    expectEveryTabVisibleAndApart();
  });

  it("gives every tab in a wrapped list the same width", async () => {
    await page.viewport(PHONE, 780);
    render(TabsListHarness, { props: { labels: LONG_LABELS, variant: "wrap" } });
    await expect.element(page.getByRole("tab", { name: LONG_LABELS[0] })).toBeVisible();

    const widths = tabs().map((tab) => Math.round(tab.getBoundingClientRect().width));
    expect(Math.max(...widths) - Math.min(...widths)).toBeLessThanOrEqual(1);
  });

  it("keeps tabs that fit in one row", async () => {
    await page.viewport(1024, 780);
    render(TabsListHarness, { props: { labels: ["Active", "History", "Presets"], variant: "wrap" } });
    await expect.element(page.getByRole("tab", { name: "Active" })).toBeVisible();

    expect(rowTops()).toHaveLength(1);
    expectEveryTabVisibleAndApart();
  });

  it("keeps the intrinsic width of a list that is not asked to wrap", async () => {
    await page.viewport(1024, 780);
    render(TabsListHarness, { props: { labels: ["On", "Off"] } });
    await expect.element(page.getByRole("tab", { name: "On" })).toBeVisible();

    expect(list().getBoundingClientRect().width).toBeLessThan(300);
  });

  it("fills the width of its container when wrapping", async () => {
    await page.viewport(PHONE, 780);
    render(TabsListHarness, { props: { labels: ["On", "Off"], variant: "wrap" } });
    await expect.element(page.getByRole("tab", { name: "On" })).toBeVisible();

    expect(list().getBoundingClientRect().width).toBeGreaterThan(PHONE - 64);
    expectEveryTabVisibleAndApart();
  });

  it("stretches the neighbours of a tab whose label wraps to the same height", async () => {
    await page.viewport(PHONE, 780);
    render(TabsListHarness, { props: { labels: ["Voreinstellungen und Benachrichtigungen", "On", "Off", "Up"], variant: "wrap" } });
    await expect.element(page.getByRole("tab", { name: "On" })).toBeVisible();

    const [long, short] = tabs().map((tab) => tab.getBoundingClientRect());
    expect(long.height).toBeGreaterThan(40);
    expect(Math.abs(long.height - short.height)).toBeLessThanOrEqual(1);
  });

  it("stacks each tile's content and colours the active tile in dark mode", async () => {
    document.documentElement.classList.add("dark");
    try {
      await page.viewport(PHONE, 780);
      render(TabsListHarness, { props: { labels: ["All", "Bolus", "Carbs"], variant: "tiles" } });
      await expect.element(page.getByRole("tab", { name: "All" })).toBeVisible();

      const [active, inactive] = tabs();
      expect(getComputedStyle(active).flexDirection).toBe("column");
      const probe = document.createElement("span");
      probe.className = "text-primary";
      document.body.append(probe);
      expect(getComputedStyle(active).color).toBe(getComputedStyle(probe).color);
      expect(getComputedStyle(inactive).color).not.toBe(getComputedStyle(probe).color);
      probe.remove();
    } finally {
      document.documentElement.classList.remove("dark");
    }
  });
});
