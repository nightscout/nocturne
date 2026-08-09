import { describe, it, expect } from "vitest";
import { render } from "vitest-browser-svelte";
import { flushSync } from "svelte";
import Harness from "./ContextResourceHarness.test.svelte";
import type { ReportsParamsReturn } from "./date-params.svelte";

/**
 * A resource asked for with `dateParams` must stay live. Building the returned object
 * with `{ ...base, get date() }` instead reads every getter on `base` once and copies
 * the results as plain values, so the caller keeps whatever `current`/`loading` held
 * during component init — undefined and true. The layout's ResourceGuard reads the
 * query through its own closure and so still showed content, which is why the seven
 * reports that asked for `date` (overview, AGP, executive summary, treatments, insulin
 * delivery, site change impact, IDP) rendered their empty state forever while the
 * network showed the data arriving.
 */
describe("contextResource with dateParams", () => {
  function setup() {
    let current = $state<string | undefined>(undefined);
    let loading = $state(true);
    let dayCount = $state(7);

    const query = {
      get loading() {
        return loading;
      },
      get error() {
        return null;
      },
      get current() {
        return current;
      },
      refresh() {},
    };

    const dateParams = {
      get startDate() {
        return new Date("2026-01-01T00:00:00Z");
      },
      get endDate() {
        return new Date("2026-01-07T23:59:59Z");
      },
      get dayCount() {
        return dayCount;
      },
    } as unknown as ReportsParamsReturn;

    const screen = render(Harness, { query, dateParams });

    return {
      screen,
      resolve(value: string) {
        current = value;
        loading = false;
        flushSync();
      },
      setDayCount(value: number) {
        dayCount = value;
        flushSync();
      },
    };
  }

  it("surfaces the value once the query resolves", async () => {
    const { screen, resolve } = setup();

    await expect.element(screen.getByTestId("current")).toHaveTextContent("none");
    await expect.element(screen.getByTestId("loading")).toHaveTextContent("true");

    resolve("REPORT DATA");

    await expect.element(screen.getByTestId("current")).toHaveTextContent("REPORT DATA");
    await expect.element(screen.getByTestId("loading")).toHaveTextContent("false");
  });

  it("keeps `date` live as the selected range changes", async () => {
    const { screen, setDayCount } = setup();

    await expect.element(screen.getByTestId("day-count")).toHaveTextContent("7");

    setDayCount(30);

    await expect.element(screen.getByTestId("day-count")).toHaveTextContent("30");
  });
});
