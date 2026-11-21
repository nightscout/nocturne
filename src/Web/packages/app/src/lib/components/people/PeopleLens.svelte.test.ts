import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, it, expect, vi } from "vitest";
import { page as pageState } from "$app/state";
import type {
  TenantOverviewResponse,
  TenantReviewResponse,
} from "#lib/api/generated/nocturne-api-client.js";
import { formatMediumDateRange } from "#lib/utils/formatting.js";
import {
  makePerson,
  makeReview,
  makeReviewEntry,
  makeStatistics,
} from "./people-fixtures.js";

const overview = $state<{ data: TenantOverviewResponse | undefined }>({
  data: undefined,
});

const review = $state<{
  data: TenantReviewResponse | undefined;
  error: unknown;
  loading: boolean;
}>({ data: undefined, error: undefined, loading: false });

const byDays = $state<Record<number, TenantReviewResponse | undefined>>({});

const overviewRefresh = vi.hoisted(() => ({ run: () => {} }));
const reviewRefresh = vi.hoisted(() => ({ run: async () => {} }));
const reviewCalls = vi.hoisted(() => ({ args: [] as unknown[] }));
const poll = vi.hoisted(() => ({ tick: () => {} }));
const nav = vi.hoisted(() => ({ afterNavigate: () => {} }));

vi.mock("$app/navigation", () => ({
  replaceState: (url: string | URL) => {
    pageState.url = new URL(url, pageState.url.href);
  },
  afterNavigate: (callback: () => void) => {
    nav.afterNavigate = callback;
  },
}));

vi.mock("#lib/utils/poll-while-visible.svelte.js", () => ({
  pollWhileVisible: (tick: () => void) => {
    poll.tick = tick;
  },
}));

vi.mock("#lib/api/generated/myTenants.generated.remote.js", () => ({
  getPerson: () => {
    throw new Error("No person is selected");
  },
  getOverview: () => ({
    get current() {
      return overview.data;
    },
    error: undefined,
    loading: false,
    refresh: () => overviewRefresh.run(),
  }),
  getReview: (args: unknown) => {
    reviewCalls.args.push(args);
    return {
      get current() {
        const days = (args as { days: number }).days;
        return Object.hasOwn(byDays, days) ? byDays[days] : review.data;
      },
      get error() {
        return review.error;
      },
      get loading() {
        return review.loading;
      },
      refresh: () => reviewRefresh.run(),
    };
  },
}));

import PeoplePage from "./PeoplePage.svelte";

function crowd(count: number) {
  return Array.from({ length: count }, (_, i) =>
    makePerson({ slug: `person${i}` })
  );
}

function reviewOf(people: ReturnType<typeof crowd>): TenantReviewResponse {
  return makeReview(people.map((p) => makeReviewEntry(p)));
}

function linkedNames() {
  return page
    .getByTestId("person-row")
    .elements()
    .map((link) => link.querySelector("p")?.textContent?.trim());
}

function checked(name: string) {
  return page.getByRole("radio", { name }).element().getAttribute("aria-checked");
}

function openAt(search: string) {
  pageState.url = new URL(`http://localhost/${search}`);
  render(PeoplePage);
}

beforeEach(async () => {
  await page.viewport(1100, 900);
  pageState.data = { baseDomain: "example.com" };
  pageState.url = new URL("http://localhost/");
  overview.data = undefined;
  review.data = undefined;
  for (const key of Object.keys(byDays)) delete byDays[Number(key)];
  review.error = undefined;
  review.loading = false;
  overviewRefresh.run = () => {};
  reviewRefresh.run = async () => {};
  reviewCalls.args = [];
});

describe("PeoplePage lens", () => {
  it("opens on Now and reads no statistics", async () => {
    overview.data = { tenants: crowd(5) };
    render(PeoplePage);

    await expect.element(page.getByText("Reading age")).toBeVisible();
    expect(checked("Now")).toBe("true");
    expect(page.getByRole("radio", { name: "14 days" }).elements()).toHaveLength(0);
    expect(reviewCalls.args).toHaveLength(0);
  });

  it("switches the data columns without changing the people, and offers the three periods", async () => {
    const people = crowd(5);
    overview.data = { tenants: people };
    review.data = reviewOf(people);
    render(PeoplePage);
    await expect.element(page.getByText("Reading age")).toBeVisible();
    const before = linkedNames();

    await page.getByRole("radio", { name: "Review" }).click();

    await expect.element(page.getByText("Distribution")).toBeVisible();
    expect(page.getByText("Reading age").elements()).toHaveLength(0);
    expect(linkedNames()).toEqual(before);
    for (const name of ["14 days", "30 days", "90 days"]) {
      await expect.element(page.getByRole("radio", { name })).toBeVisible();
    }
    expect(checked("14 days")).toBe("true");

    await page.getByRole("radio", { name: "Now" }).click();
    await expect.element(page.getByText("Reading age")).toBeVisible();
    expect(page.getByRole("radio", { name: "14 days" }).elements()).toHaveLength(0);
  });

  it("writes the lens and the period to the URL, and drops both for Now", async () => {
    const people = crowd(5);
    overview.data = { tenants: people };
    review.data = reviewOf(people);
    render(PeoplePage);
    await expect.element(page.getByText("Reading age")).toBeVisible();

    await page.getByRole("radio", { name: "Review" }).click();
    await expect.poll(() => pageState.url.search).toBe("?lens=review&days=14");

    await page.getByRole("radio", { name: "90 days" }).click();
    await expect.poll(() => pageState.url.search).toBe("?lens=review&days=90");

    await page.getByRole("radio", { name: "Now" }).click();
    await expect.poll(() => pageState.url.search).toBe("");
  });

  it("opens in the lens and period the URL names", async () => {
    const people = crowd(5);
    overview.data = { tenants: people };
    review.data = reviewOf(people);
    openAt("?lens=review&days=90");

    await expect.element(page.getByText("Distribution")).toBeVisible();
    expect(checked("90 days")).toBe("true");
    expect(reviewCalls.args.at(-1)).toEqual({ days: 90 });
  });

  it.each(["?lens=nonsense&days=30", "?days=30", "?lens=REVIEW"])(
    "falls back to Now for %s and reads no statistics",
    async (search) => {
      overview.data = { tenants: crowd(5) };
      openAt(search);

      await expect.element(page.getByText("Reading age")).toBeVisible();
      expect(checked("Now")).toBe("true");
      expect(reviewCalls.args).toHaveLength(0);
    }
  );

  it.each(["7", "", "abc", "30.5"])(
    "falls back to 14 days for days=%s",
    async (days) => {
      const people = crowd(5);
      overview.data = { tenants: people };
      review.data = reviewOf(people);
      openAt(`?lens=review&days=${days}`);

      await expect.element(page.getByText("Distribution")).toBeVisible();
      expect(checked("14 days")).toBe("true");
      expect(reviewCalls.args.at(-1)).toEqual({ days: 14 });
    }
  );

  it("reads the statistics again for a new period", async () => {
    const people = crowd(5);
    overview.data = { tenants: people };
    review.data = reviewOf(people);
    openAt("?lens=review&days=14");
    await expect.element(page.getByText("Distribution")).toBeVisible();
    expect(reviewCalls.args.at(-1)).toEqual({ days: 14 });

    await page.getByRole("radio", { name: "30 days" }).click();

    await expect.poll(() => reviewCalls.args.at(-1)).toEqual({ days: 30 });
  });

  it("refreshes the overview on its poll and does not read the statistics again", async () => {
    const people = crowd(5);
    overview.data = { tenants: people };
    review.data = reviewOf(people);
    let overviewReads = 0;
    let reviewReads = 0;
    overviewRefresh.run = () => {
      overviewReads += 1;
    };
    reviewRefresh.run = async () => {
      reviewReads += 1;
    };
    openAt("?lens=review&days=14");
    await expect.element(page.getByText("Distribution")).toBeVisible();
    const callsBefore = reviewCalls.args.length;

    poll.tick();
    poll.tick();

    expect(overviewReads).toBe(2);
    expect(reviewReads).toBe(0);
    expect(reviewCalls.args).toHaveLength(callsBefore);
  });

  it("states the period in the subtitle in Review", async () => {
    const people = crowd(5);
    overview.data = { tenants: people };
    review.data = reviewOf(people);
    openAt("?lens=review&days=14");

    const range = formatMediumDateRange(
      new Date("2026-11-07T00:00:00Z"),
      new Date("2026-11-21T00:00:00Z")
    );
    await expect
      .element(page.getByText(`5 people. Statistics for ${range}.`))
      .toBeVisible();
    expect(page.getByText("Readings refresh every minute.").elements()).toHaveLength(0);
  });

  it("shows a line and a retry above the list when the read failed, and keeps the person cells", async () => {
    const people = crowd(5);
    overview.data = { tenants: people };
    review.error = new Error("boom");
    reviewRefresh.run = async () => {
      review.error = undefined;
      review.data = reviewOf(people);
    };
    openAt("?lens=review&days=14");

    await expect
      .element(page.getByText("The statistics did not load."))
      .toBeVisible();
    expect(linkedNames()).toEqual(people.map((p) => p.displayName));
    expect(page.getByTestId("review-notice").elements()).toHaveLength(0);

    await page.getByRole("button", { name: "Try again" }).click();

    await expect.element(page.getByTestId("review-gmi").first()).toBeVisible();
    expect(page.getByText("The statistics did not load.").elements()).toHaveLength(0);
  });

  it("shows placeholders in the data cells while the read is pending", async () => {
    const people = crowd(5);
    overview.data = { tenants: people };
    review.loading = true;
    openAt("?lens=review&days=14");

    await expect.element(page.getByTestId("review-loading").first()).toBeVisible();
    expect(page.getByTestId("review-loading").elements()).toHaveLength(5);
    expect(linkedNames()).toEqual(people.map((p) => p.displayName));
  });

  it("uses the large layout in Review at 4 people and names the chosen period", async () => {
    const people = crowd(4);
    overview.data = { tenants: people };
    review.data = makeReview(
      people.map((p) =>
        makeReviewEntry(p, {
          statistics: makeStatistics({ timeInRangeChange: -3 }),
        })
      )
    );
    openAt("?lens=review&days=30");

    await expect.element(page.getByText("Person0")).toBeVisible();
    expect(
      page.getByTestId("review-change-sentence").first().element().textContent?.trim()
    ).toBe("Down 3 points from the previous 30 days.");
  });

  it("never shows one period's figures under another", async () => {
    const people = crowd(5);
    overview.data = { tenants: people };
    byDays[14] = makeReview(
      people.map((p) =>
        makeReviewEntry(p, { statistics: makeStatistics({ gmi: 7.1 }) })
      )
    );
    byDays[30] = undefined;
    openAt("?lens=review&days=14");
    await expect.element(page.getByTestId("review-gmi").first()).toHaveTextContent("7.1%");

    await page.getByRole("radio", { name: "30 days" }).click();

    await expect.element(page.getByTestId("review-loading").first()).toBeVisible();
    expect(page.getByTestId("review-gmi").elements()).toHaveLength(0);
    expect(document.body.textContent).not.toContain("7.1%");

    byDays[30] = makeReview(
      people.map((p) =>
        makeReviewEntry(p, { statistics: makeStatistics({ gmi: 8.5 }) })
      )
    );
    await expect.element(page.getByTestId("review-gmi").first()).toHaveTextContent("8.5%");
    expect(document.body.textContent).not.toContain("7.1%");
  });

  it("follows the URL when a navigation lands on the same page", async () => {
    const people = crowd(5);
    overview.data = { tenants: people };
    review.data = reviewOf(people);
    openAt("?lens=review&days=30");
    await expect.element(page.getByText("Distribution")).toBeVisible();

    pageState.url = new URL("http://localhost/");
    nav.afterNavigate();
    await expect.element(page.getByText("Reading age")).toBeVisible();
    expect(checked("Now")).toBe("true");

    pageState.url = new URL("http://localhost/?lens=review&days=90");
    nav.afterNavigate();
    await expect.element(page.getByText("Distribution")).toBeVisible();
    expect(checked("90 days")).toBe("true");
  });

  it.each([320, 390])(
    "keeps the title at full width and puts both controls on their own row at %ipx",
    async (width) => {
      await page.viewport(width, 900);
      const people = crowd(5);
      overview.data = { tenants: people };
      review.data = reviewOf(people);
      openAt("?lens=review&days=14");

      const heading = page.getByRole("heading", { name: "People", level: 1 });
      await expect.element(heading).toBeVisible();
      const subtitle = page.getByText(/^5 people\. Statistics for/);
      await expect.element(subtitle).toBeVisible();
      const title = heading.element().getBoundingClientRect();
      const lines = subtitle.element().getBoundingClientRect().height / 20;
      expect(lines).toBeLessThanOrEqual(3);
      expect(subtitle.element().getBoundingClientRect().width).toBeGreaterThan(width - 150);
      for (const name of ["View", "Period"]) {
        const group = page.getByRole("group", { name }).element().getBoundingClientRect();
        expect(group.top).toBeGreaterThanOrEqual(title.bottom);
        expect(group.width).toBeGreaterThan(width - 60);
      }
      expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(width);
    }
  );
});
