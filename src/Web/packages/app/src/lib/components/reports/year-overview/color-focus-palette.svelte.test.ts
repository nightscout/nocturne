import type {} from "@vitest/browser-playwright";
import { describe, expect, it } from "vitest";
import { render } from "vitest-browser-svelte";
import { page, cdp } from "vitest/browser";
import Preview from "./ColorFocusPreview.test-harness.svelte";

const background = (id: string) => {
  const context = document.createElement("canvas").getContext("2d")!;
  context.fillStyle = getComputedStyle(
    page.getByTestId(id).element()
  ).backgroundColor;
  context.fillRect(0, 0, 1, 1);
  return [...context.getImageData(0, 0, 1, 1).data];
};

describe("very low average glucose colors", () => {
  it.each(["", "trio-theme", "aaps-theme", "classic-theme"])(
    "separates the below-3 ramp and missing data in light and dark %s",
    async (theme) => {
      const root = document.documentElement;
      const original = root.className;
      await page.viewport(640, 580);
      try {
        root.className = theme;
        render(Preview);
        await expect
          .element(
            page.getByRole("img", { name: /Average glucose color scale from/ })
          )
          .toBeVisible();
        const light = background("sample-40");
        expect(light).toEqual(background("sample-54"));
        expect(light).toEqual(background("sample-45"));
        expect(background("sample-63")).not.toEqual(background("sample-72"));
        expect(background("sample-45")).not.toEqual(background("sample-63"));
        expect(light).not.toEqual(background("empty-sample"));
        await page.screenshot({
          path: `test-results/color-${theme || "nocturne"}-light.png`,
        });
        root.classList.add("dark");
        const dark = background("sample-40");
        expect(dark).not.toEqual(light);
        expect(dark).toEqual(background("sample-54"));
        expect(dark).toEqual(background("sample-45"));
        expect(dark).not.toEqual(background("empty-sample"));
        await page.screenshot({
          path: `test-results/color-${theme || "nocturne"}-dark.png`,
        });
        await cdp().send("Emulation.setEmulatedMedia", { media: "print" });
        expect(background("sample-40")).toEqual(light);
        expect(background("preview-card")).toEqual([255, 255, 255, 255]);
        await expect
          .element(page.getByRole("slider", { includeHidden: true }).first())
          .not.toBeVisible();
        await page.screenshot({
          path: `test-results/color-${theme || "nocturne"}-print.png`,
        });
      } finally {
        await cdp().send("Emulation.setEmulatedMedia", { media: "screen" });
        root.className = original;
      }
    }
  );
});
