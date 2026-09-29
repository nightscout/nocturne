import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import { createRawSnippet } from "svelte";
import { GlucoseStatus } from "$lib/api/generated/nocturne-api-client";
import { getGlucoseTileVariant } from "$lib/utils/glucose-status";
import { GlucoseValueIndicator } from "./index";

function renderReading(status: GlucoseStatus | undefined, isStale = false) {
  render(GlucoseValueIndicator, {
    displayValue: 170,
    variant: getGlucoseTileVariant(status),
    isStale,
  });
  return page.getByText("170");
}

describe("GlucoseValueIndicator", () => {
  it.each([
    [
      GlucoseStatus.UrgentLow,
      "bg-glucose-very-low",
      "text-glucose-very-low-foreground",
    ],
    [GlucoseStatus.Low, "bg-glucose-low", "text-glucose-low-foreground"],
    [
      GlucoseStatus.InRange,
      "bg-glucose-in-range",
      "text-glucose-in-range-foreground",
    ],
    [GlucoseStatus.High, "bg-glucose-high", "text-glucose-high-foreground"],
    [
      GlucoseStatus.UrgentHigh,
      "bg-glucose-very-high",
      "text-glucose-very-high-foreground",
    ],
    [GlucoseStatus.Stale, "bg-muted", "text-muted-foreground"],
    [GlucoseStatus.Unknown, "bg-muted", "text-muted-foreground"],
  ])("fills a %s reading with %s and %s", async (status, fill, text) => {
    const tile = renderReading(status);
    await expect.element(tile).toHaveClass(fill);
    await expect.element(tile).toHaveClass(text);
  });

  it("takes its colour from the server status, not the value", async () => {
    render(GlucoseValueIndicator, {
      displayValue: 170,
      variant: getGlucoseTileVariant(GlucoseStatus.InRange),
    });
    render(GlucoseValueIndicator, {
      displayValue: 170,
      variant: getGlucoseTileVariant(GlucoseStatus.High),
    });

    const [inRange, high] = page.getByText("170").elements();
    expect(inRange).toHaveClass("bg-glucose-in-range");
    expect(high).toHaveClass("bg-glucose-high");
  });

  it("is neutral before the status for the reading has loaded", async () => {
    await expect.element(renderReading(undefined)).toHaveClass("bg-muted");
  });

  it("is neutral while stale, whatever the last status was", async () => {
    const tile = renderReading(GlucoseStatus.UrgentLow, true);
    await expect.element(tile).toHaveClass("bg-muted");
    await expect.element(tile).not.toHaveClass("bg-glucose-very-low");
  });
});

describe("GlucoseValueIndicator background", () => {
  const background = createRawSnippet(() => ({ render: () => `<i data-testid="wash"></i>` }));
  const inRange = getGlucoseTileVariant(GlucoseStatus.InRange);

  it("lays the background under the value and keeps the range fill", async () => {
    render(GlucoseValueIndicator, { displayValue: 170, variant: inRange, background });

    await expect.element(page.getByTestId("wash")).toBeInTheDocument();
    await expect.element(page.getByText("170")).toBeInTheDocument();
    expect(page.getByText("170").element().parentElement).toHaveClass("bg-glucose-in-range");
  });

  it.each([
    ["stale", { isStale: true }],
    ["disconnected", { isDisconnected: true }],
  ])("leaves the plain look when %s", async (_, state) => {
    render(GlucoseValueIndicator, { displayValue: 170, variant: inRange, background, ...state });

    await expect.element(page.getByTestId("wash")).not.toBeInTheDocument();
  });
});
