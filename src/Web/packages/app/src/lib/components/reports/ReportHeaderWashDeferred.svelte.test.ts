import { render } from "vitest-browser-svelte";
import { afterEach, describe, expect, it, vi } from "vitest";
import { illustrations } from "#lib/stores/illustrations.svelte.js";
import { releaseWatercolour, resetWatercolour } from "#lib/test-stubs/watercolour-deferral.svelte.js";
import ReportHeaderWashDeferred from "./ReportHeaderWashDeferred.svelte";

vi.mock("#lib/watercolour-deferral.svelte.js", () => import("#lib/test-stubs/watercolour-deferral.svelte.js"));

const wash = (container: HTMLElement) => container.querySelector("canvas");

describe("ReportHeaderWashDeferred", () => {
  afterEach(() => {
    illustrations.current = "animated";
    resetWatercolour();
  });

  it("paints nothing until the first-interaction window is over, then the wash", async () => {
    const { container } = render(ReportHeaderWashDeferred, { name: "/reports/agp" });
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(wash(container)).toBeNull();

    releaseWatercolour();

    await vi.waitFor(() => expect(wash(container)).not.toBeNull());
  });

  it("mounts at once under the Still preference", async () => {
    illustrations.current = "still";
    const { container } = render(ReportHeaderWashDeferred, { name: "/reports/agp" });

    await vi.waitFor(() => expect(wash(container)).not.toBeNull());
  });

  it("never mounts with illustrations off", async () => {
    illustrations.current = "off";
    releaseWatercolour();
    const { container } = render(ReportHeaderWashDeferred, { name: "/reports/agp" });

    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(wash(container)).toBeNull();
  });
});
