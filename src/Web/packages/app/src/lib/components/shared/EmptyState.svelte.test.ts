import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createRawSnippet } from "svelte";
import { Clock } from "lucide";
import { resetCapabilitiesCache, type IconArtworkSource } from "@nocturne/watercolour";
import EmptyState from "./EmptyState.svelte";

// No `lucide-hourglass-test` set is baked, so without a GPU this can only reach the plain-SVG rung.
const unbakedIcon = { icon: Clock, name: "hourglass-test" } satisfies IconArtworkSource;

function withoutGpu(reducedMotion: boolean): void {
  Object.defineProperty(navigator, "gpu", { configurable: true, get: () => undefined });
  const realMatchMedia = window.matchMedia.bind(window);
  vi.spyOn(window, "matchMedia").mockImplementation((query: string) =>
    query.includes("prefers-reduced-motion")
      ? ({ ...realMatchMedia(query), matches: reducedMotion, media: query })
      : realMatchMedia(query)
  );
  resetCapabilitiesCache();
}

async function paintedPixels(canvas: HTMLCanvasElement): Promise<number> {
  let count = 0;
  await expect
    .poll(
      () => {
        const current = canvas.parentElement?.querySelector("canvas") ?? canvas;
        const ctx = current.getContext("2d");
        if (!ctx || current.width === 0) return 0;
        const { data } = ctx.getImageData(0, 0, current.width, current.height);
        count = 0;
        for (let i = 3; i < data.length; i += 4) if (data[i]! > 0) count++;
        return count;
      },
      { timeout: 10_000 }
    )
    .toBeGreaterThan(0);
  return count;
}

function artCanvas(container: HTMLElement): HTMLCanvasElement {
  const canvas = container.querySelector("canvas");
  if (!canvas) throw new Error("EmptyState rendered no artwork canvas");
  return canvas;
}

const action = createRawSnippet(() => ({ render: () => "<button>Create a token</button>" }));

describe("EmptyState", () => {
  afterEach(() => {
    vi.restoreAllMocks();
    Reflect.deleteProperty(navigator, "gpu");
    resetCapabilitiesCache();
  });

  it("renders the title, body and action slot", async () => {
    render(EmptyState, {
      art: "key",
      title: "No API tokens yet",
      body: "Create one to let an uploader app send readings.",
      action,
    });

    await expect.element(page.getByText("No API tokens yet")).toBeVisible();
    await expect.element(page.getByText("Create one to let an uploader app send readings.")).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Create a token" })).toBeVisible();
  });

  it("renders no action row without an action", async () => {
    render(EmptyState, { art: "key", title: "No API tokens yet" });

    await expect.element(page.getByText("No API tokens yet")).toBeVisible();
    expect(page.getByRole("button").elements()).toHaveLength(0);
  });

  it("keeps the painting out of the accessibility tree", async () => {
    const { container } = render(EmptyState, { art: "key", title: "No API tokens yet" });

    expect(artCanvas(container).parentElement?.getAttribute("aria-hidden")).toBe("true");
  });

  it("draws a smaller painting at the compact size", async () => {
    const { container } = render(EmptyState, { art: "clock", title: "No history yet", size: "compact" });
    const compact = artCanvas(container).parentElement!.getBoundingClientRect().width;

    const { container: full } = render(EmptyState, { art: "clock", title: "No history yet" });
    const standard = artCanvas(full).parentElement!.getBoundingClientRect().width;

    expect(compact).toBeGreaterThan(0);
    expect(compact).toBeLessThan(standard);
  });

  it("wraps itself in a dashed card for the dashed variant", async () => {
    const { container } = render(EmptyState, {
      art: "key",
      title: "No guest links yet",
      variant: "dashed",
      "data-testid": "guest-links-empty",
    });

    const card = container.querySelector('[data-testid="guest-links-empty"]');
    expect(card?.getAttribute("data-slot")).toBe("card");
    expect(card?.className).toContain("border-dashed");
  });

  it("renders the title as a paragraph unless given a heading level", async () => {
    render(EmptyState, { art: "report-pages", title: "No sleep data", headingLevel: 2 });
    await expect.element(page.getByRole("heading", { level: 2, name: "No sleep data" })).toBeVisible();

    render(EmptyState, { art: "report-pages", title: "No recent meals", size: "compact" });
    await expect.element(page.getByText("No recent meals")).toBeVisible();
    expect(page.getByRole("heading", { name: "No recent meals" }).elements()).toHaveLength(0);
  });

  it("draws the outline variant as a dashed border with no card", async () => {
    const { container } = render(EmptyState, {
      art: "stopwatch",
      title: "No definitions yet",
      variant: "outline",
      "data-testid": "definitions-empty",
    });

    const outline = container.querySelector('[data-testid="definitions-empty"]');
    expect(outline?.getAttribute("data-slot")).toBeNull();
    expect(outline?.className).toContain("border-dashed");
    expect(container.querySelector('[data-slot="card"]')).toBeNull();
  });

  it("renders the footnote after the actions", async () => {
    const footnote = createRawSnippet(() => ({ render: () => "<p>Or browse the food bank</p>" }));
    render(EmptyState, { art: "apple", title: "Build your food database", action, footnote });

    const button = page.getByRole("button", { name: "Create a token" }).element();
    const note = page.getByText("Or browse the food bank").element();
    expect(button.compareDocumentPosition(note) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  describe("without a GPU", () => {
    for (const reducedMotion of [false, true]) {
      const label = reducedMotion ? "under reduced motion" : "with motion";

      describe(label, () => {
        beforeEach(() => withoutGpu(reducedMotion));

        it("paints a catalogue artwork from its baked asset", async () => {
          const { container } = render(EmptyState, { art: "key", title: "No API tokens yet" });

          expect(await paintedPixels(artCanvas(container))).toBeGreaterThan(0);
        });

        it("draws an unbaked icon source as the plain Lucide icon", async () => {
          const { container } = render(EmptyState, { art: unbakedIcon, title: "No history yet" });

          expect(await paintedPixels(artCanvas(container))).toBeGreaterThan(0);
        });
      });
    }
  });
});
