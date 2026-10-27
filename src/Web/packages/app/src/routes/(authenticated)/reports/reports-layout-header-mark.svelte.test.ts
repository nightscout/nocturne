import { render } from "vitest-browser-svelte";
import { describe, expect, it, vi } from "vitest";
import type { CoachMarkContext } from "@nocturne/coach";
import { page as pageState } from "$app/state";
import { sequences } from "#lib/coach-marks/sequences.js";
import Harness from "./ReportsLayoutHarness.test.svelte";

vi.mock("$app/state", async () => {
  const stub = await vi.importActual<typeof import("$app/state")>("$app/state");
  const { reactiveFields } = await import("#lib/test-stubs/reactive-fields.svelte.js");
  return { ...stub, page: reactiveFields({ ...stub.page }) };
});

const SWITCHER = sequences["loop-analysis-pages"].steps[0];

async function layoutAt(path: string) {
  pageState.url = new URL(`http://localhost${path}`) as typeof pageState.url;
  for (const key of Object.keys(pageState.data)) delete pageState.data[key];
  Object.assign(pageState.data, { user: { id: "member" } });

  let ctx: CoachMarkContext | undefined;
  render(Harness, { oncontext: (c: CoachMarkContext) => (ctx = c) });
  await vi.waitFor(() => expect(ctx).toBeDefined());
  await vi.waitFor(() => expect(document.querySelector("h1")).toBeTruthy());
  return ctx!;
}

describe("the reports layout header", () => {
  it("carries the Loop Analysis pages and filters mark on a Loop Analysis page", async () => {
    const ctx = await layoutAt("/reports/loop-analysis/daily-profile");

    const [mark] = ctx.getMountedSteps(SWITCHER);

    expect(mark.element.matches(".sticky")).toBe(true);
    expect(mark.element.querySelector("h1")?.textContent).toContain("Loop analysis");
  });

  it("carries no mark on another report", async () => {
    const ctx = await layoutAt("/reports/agp");

    expect(ctx.getMountedSteps(SWITCHER)).toEqual([]);
  });
});
