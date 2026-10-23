import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import { remoteQuery } from "$lib/test-stubs/remote-resource";
import { ALL_SCOPES, profileResponse } from "$lib/components/reports/loop-analysis/daily-profile.test-fixtures";
import Harness from "./FiltersPanelHarness.test.svelte";

/** The browser history as SvelteKit's goto leaves it: a push adds an entry, a replace swaps the last. */
const nav = vi.hoisted(() => ({ history: [] as string[], fetched: [] as unknown[] }));

vi.mock("$app/state", async () => {
  const stub = await vi.importActual<typeof import("$app/state")>("$app/state");
  const { reactiveFields } = await import("$lib/test-stubs/reactive-fields.svelte");
  return { ...stub, page: reactiveFields({ ...stub.page }) };
});

vi.mock("$app/navigation", async () => {
  const stub = await vi.importActual<typeof import("$app/navigation")>("$app/navigation");
  const { page: state } = await import("$app/state");
  return {
    ...stub,
    goto: (url: string, options?: { replaceState?: boolean }) => {
      const next = new URL(url, state.url);
      if (options?.replaceState) nav.history[nav.history.length - 1] = next.href;
      else nav.history.push(next.href);
      state.url = next as typeof state.url;
      return Promise.resolve();
    },
  };
});

vi.mock("$api/generated/loopAnalysis.generated.remote", () => ({
  getEnvelope: () => remoteQuery(() => profileResponse()),
  getDailyProfile: (input: unknown) => {
    nav.fetched.push(input);
    return remoteQuery(() => profileResponse());
  },
}));

vi.mock("$lib/hooks/resource-context.svelte", () => ({
  contextResource: <T>(create: () => { current: T | undefined }) => ({
    get current() {
      return create().current;
    },
    stale: false,
    error: null,
  }),
}));

function back() {
  nav.history.pop();
  pageState.url = new URL(nav.history.at(-1)!) as typeof pageState.url;
}

const START = "http://localhost/reports/loop-analysis/daily-profile?days=14&isDefault=false&weekdays=6";

describe("the reports Filters panel on a Loop Analysis page", () => {
  beforeEach(() => {
    nav.history = [START];
    nav.fetched.length = 0;
    pageState.url = new URL(START) as typeof pageState.url;
    for (const key of Object.keys(pageState.data)) delete pageState.data[key];
    Object.assign(pageState.data, { effectivePermissions: ALL_SCOPES, user: { id: "member" } });
  });

  it("applies the range and the Loop Analysis filters as one history entry that Back undoes", async () => {
    render(Harness);
    await expect.element(page.getByRole("button", { name: "Open filters" })).toBeVisible();
    const before = nav.history.length;

    await page.getByRole("button", { name: "Open filters" }).click();
    await page.getByLabelText("Exercise").click();
    await page.getByRole("button", { name: "7 Days" }).click();
    await page.getByRole("button", { name: "Apply Filters" }).click();

    await expect.poll(() => nav.history.length).toBe(before + 1);
    const applied = new URL(nav.history.at(-1)!);
    expect(applied.pathname).toBe("/reports/loop-analysis/daily-profile");
    expect(applied.searchParams.get("days")).toBe("7");
    expect(applied.searchParams.get("isDefault")).toBe("false");
    expect(applied.searchParams.get("excludeExercise")).toBe("true");
    expect(applied.searchParams.get("weekdays")).toBe("6");
    await expect.poll(() => nav.fetched.at(-1)).toMatchObject({ days: [6], excludeExercise: true });

    back();
    expect(pageState.url.href).toBe(nav.history.at(-1));
    expect(pageState.url.searchParams.has("excludeExercise")).toBe(false);
    expect(pageState.url.searchParams.get("weekdays")).toBe("6");
    await expect.poll(() => nav.fetched.at(-1)).not.toHaveProperty("excludeExercise");
  });

  it("applies a Loop Analysis filter alone as one history entry, keeping the range", async () => {
    render(Harness);
    await expect.element(page.getByRole("button", { name: "Open filters" })).toBeVisible();
    const before = nav.history.length;
    const range = new URL(nav.history.at(-1)!).searchParams;

    await page.getByRole("button", { name: "Open filters" }).click();
    await page.getByLabelText("Overrides").click();
    await page.getByRole("button", { name: "Apply Filters" }).click();

    await expect.poll(() => nav.history.length).toBe(before + 1);
    const applied = new URL(nav.history.at(-1)!).searchParams;
    expect(applied.get("excludeOverrides")).toBe("true");
    expect(applied.get("days")).toBe(range.get("days"));
    expect(applied.get("weekdays")).toBe("6");
  });

  it("resets the range and the Loop Analysis filters as one history entry", async () => {
    nav.history = [`${START}&excludeExercise=true`];
    pageState.url = new URL(nav.history[0]) as typeof pageState.url;
    render(Harness);
    await expect.element(page.getByRole("button", { name: "Open filters" })).toBeVisible();
    const before = nav.history.length;

    await page.getByRole("button", { name: "Open filters" }).click();
    await page.getByRole("button", { name: "Reset" }).click();

    await expect.poll(() => nav.history.length).toBe(before + 1);
    const reset = new URL(nav.history.at(-1)!).searchParams;
    expect(reset.has("excludeExercise")).toBe(false);
    expect(reset.has("weekdays")).toBe(false);
    expect(reset.get("isDefault")).toBe("true");
  });
});
