import type {} from "@vitest/browser-playwright";
import { cdp } from "vitest/browser";
import { afterEach, describe, expect, it } from "vitest";
import { PRINT_LAYOUT_CLASS } from "#lib/components/charts/print/print-mode.svelte.js";

const root = document.documentElement;

function mountGrid(): { first: HTMLElement; second: HTMLElement; remove: () => void } {
  const grid = document.createElement("div");
  grid.className = "grid w-[400px] grid-cols-1 print:grid-cols-2";
  grid.innerHTML = "<div>a</div><div>b</div>";
  document.body.append(grid);
  return {
    first: grid.children[0] as HTMLElement,
    second: grid.children[1] as HTMLElement,
    remove: () => grid.remove(),
  };
}

async function emulateMedia(media: "print" | "screen"): Promise<void> {
  await cdp().send("Emulation.setEmulatedMedia", { media });
}

const token = (name: string) => getComputedStyle(root).getPropertyValue(name).trim();

afterEach(async () => {
  root.classList.remove("dark", PRINT_LAYOUT_CLASS, "trio-theme", "aaps-theme", "classic-theme");
  await emulateMedia("screen");
});

describe("print variant", () => {
  it("lays out print: utilities while the paper layout is set in screen media", () => {
    root.classList.add("dark", PRINT_LAYOUT_CLASS);
    const { first, second, remove } = mountGrid();
    try {
      expect(second.getBoundingClientRect().top).toBe(first.getBoundingClientRect().top);
      expect(second.getBoundingClientRect().left).toBeGreaterThan(first.getBoundingClientRect().left);
    } finally {
      remove();
    }
  });

  it("leaves print: utilities off in screen media without the paper layout", () => {
    root.classList.add("dark");
    const { first, second, remove } = mountGrid();
    try {
      expect(second.getBoundingClientRect().top).toBeGreaterThan(first.getBoundingClientRect().top);
    } finally {
      remove();
    }
  });

  it("lays out print: utilities under print media", async () => {
    await emulateMedia("print");
    const { first, second, remove } = mountGrid();
    try {
      expect(second.getBoundingClientRect().top).toBe(first.getBoundingClientRect().top);
    } finally {
      remove();
    }
  });
});

describe("print paper theme", () => {
  it("keeps the dark tokens on screen", () => {
    root.classList.add("dark");
    expect(token("--background")).toBe("oklch(0.13 0.028 261.692)");
    expect(token("--card")).toBe("oklch(0.21 0.034 264.665)");
  });

  it("uses the light tokens in print whatever the colour scheme", async () => {
    root.classList.add("dark");
    await emulateMedia("print");
    expect(token("--background")).toBe("oklch(1 0 0)");
    expect(token("--foreground")).toBe("oklch(0.13 0.028 261.692)");
    expect(token("--card")).toBe("oklch(1 0 0)");
    expect(token("--card-foreground")).toBe("oklch(0.13 0.028 261.692)");
    expect(token("--muted-foreground")).toBe("oklch(0.551 0.027 264.364)");
  });

  it("does not apply dark: utilities in print", async () => {
    root.classList.add("dark");
    const probe = document.createElement("div");
    probe.className = "bg-white dark:bg-black";
    document.body.append(probe);
    try {
      const screenColour = getComputedStyle(probe).backgroundColor;
      await emulateMedia("print");
      expect(getComputedStyle(probe).backgroundColor).not.toBe(screenColour);
    } finally {
      probe.remove();
    }
  });

  it.each([
    ["trio-theme", "oklch(0.98 0.005 264)", "oklch(0.15 0.02 264)"],
    ["aaps-theme", "oklch(0.99 0.002 250)", "oklch(0.12 0.01 280)"],
    ["classic-theme", "oklch(0.96 0.003 250)", "oklch(0 0 0)"],
  ])("prints the %s light background in dark mode", async (theme, light, dark) => {
    root.classList.add(theme, "dark");
    expect(token("--background")).toBe(dark);
    await emulateMedia("print");
    expect(token("--background")).toBe(light);
  });
});
