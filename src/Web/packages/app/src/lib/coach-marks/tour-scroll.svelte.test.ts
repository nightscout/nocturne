import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import type { CoachMarkContext } from "@nocturne/coach";
import Harness from "./TourScrollHarness.test.svelte";

type Layout = "tour" | "far-only" | "two-tours" | "parts";

async function open(layout: Layout): Promise<CoachMarkContext> {
  let ctx: CoachMarkContext | undefined;
  render(Harness, { layout, oncontext: (c: CoachMarkContext) => (ctx = c) });
  await vi.waitFor(() => expect(ctx).toBeDefined());
  return ctx!;
}

function onScreen(text: string): boolean {
  const rect = page.getByText(text).element().getBoundingClientRect();
  return rect.bottom > 0 && rect.top < window.innerHeight;
}

async function readNear(): Promise<void> {
  await expect.element(page.getByRole("dialog", { name: "Near" })).toBeVisible();
}

describe("coach mark scrolling", () => {
  beforeEach(() => {
    window.scrollTo(0, 0);
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("brings the next step of the tour on screen when the reader presses Done", async () => {
    await open("tour");
    await readNear();

    await page.getByRole("button", { name: "Done", exact: true }).click();

    await expect.element(page.getByRole("dialog", { name: "Far" })).toBeVisible();
    await vi.waitFor(() => expect(onScreen("Far target")).toBe(true));
  });

  it("brings a later part of a mark on screen when the reader presses Next, and the first part on Back", async () => {
    await open("parts");
    await readNear();

    await page.getByRole("button", { name: "Next" }).click();
    await expect.element(page.getByRole("dialog", { name: "Near, part two" })).toBeVisible();
    await vi.waitFor(() => expect(onScreen("Far target")).toBe(true));

    await page.getByRole("button", { name: "Back" }).click();
    await expect.element(page.getByRole("dialog", { name: "Near" })).toBeVisible();
    await vi.waitFor(() => expect(onScreen("Near target")).toBe(true));
  });

  it("leaves the page where it is for a tip raised on its own", async () => {
    const ctx = await open("far-only");
    await vi.waitFor(() => expect(ctx.activeKey).toBe("top.far"));

    expect(window.scrollY).toBe(0);
    await expect.element(page.getByRole("dialog")).not.toBeInTheDocument();

    page.getByText("Far target").element().scrollIntoView();
    await expect.element(page.getByRole("dialog", { name: "Far" })).toBeVisible();
  });

  it("leaves the page where it is when Done hands over to another tour", async () => {
    const ctx = await open("two-tours");
    await readNear();

    await page.getByRole("button", { name: "Done", exact: true }).click();
    await vi.waitFor(() => expect(ctx.activeKey).toBe("other.far"));

    expect(window.scrollY).toBe(0);
    await expect.element(page.getByRole("dialog")).not.toBeInTheDocument();
  });

  it("jumps without a smooth scroll when the reader asks for reduced motion", async () => {
    const matchMedia = window.matchMedia.bind(window);
    vi.spyOn(window, "matchMedia").mockImplementation((query) =>
      query === "(prefers-reduced-motion: reduce)" ? { ...matchMedia(query), matches: true } : matchMedia(query),
    );
    await open("tour");
    await readNear();

    await page.getByRole("button", { name: "Done", exact: true }).click();

    expect(onScreen("Far target")).toBe(true);
  });
});
