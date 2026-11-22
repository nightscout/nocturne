import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { afterEach, beforeEach, describe, it, expect, vi } from "vitest";
import { page as pageState } from "$app/state";
import {
  TenantReviewUnavailable,
  type TenantOverviewResponse,
  type TenantReviewResponse,
} from "@nocturne/api-client";
import { formatMediumDateRange } from "#lib/utils/formatting.js";
import { OVERVIEW_PING_DEBOUNCE_MS } from "./live-overview.svelte.js";
import { overviewSockets } from "./people-socket-fixture.svelte.js";
import {
  makeDetail,
  makePerson,
  makeReview,
  makeReviewEntry,
  makeStatistics,
} from "./people-fixtures.js";

const overview = $state<{ data: TenantOverviewResponse | undefined; error: unknown }>({
  data: undefined,
  error: undefined,
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
const nav = vi.hoisted(() => ({
  afterNavigate: () => {},
  address: new URL("http://localhost/"),
}));

vi.mock("$app/navigation", () => ({
  replaceState: (url: string | URL) => {
    nav.address = new URL(url, nav.address.href);
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

vi.mock("#lib/websocket/websocket-client.svelte.js", () => import("./people-socket-fixture.svelte.js"));

vi.mock("#lib/api/generated/myTenants.generated.remote.js", () => ({
  getPerson: ({ tenantId }: { tenantId: string }) => ({
    get current() {
      const person = overview.data?.tenants?.find((p) => p.tenantId === tenantId);
      return person ? makeDetail(person) : undefined;
    },
    error: undefined,
    loading: false,
    refresh: async () => {},
  }),
  getOverview: () => ({
    get current() {
      return overview.data;
    },
    get error() {
      return overview.error;
    },
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
  nav.address = new URL(pageState.url.href);
  return render(PeoplePage);
}

function rowHrefs() {
  return page
    .getByTestId("person-row")
    .elements()
    .map((link) => link.getAttribute("href"));
}

beforeEach(async () => {
  await page.viewport(1100, 900);
  pageState.data = { baseDomain: "example.com" };
  pageState.url = new URL("http://localhost/");
  nav.address = new URL(pageState.url.href);
  overview.data = undefined;
  overview.error = undefined;
  review.data = undefined;
  for (const key of Object.keys(byDays)) delete byDays[Number(key)];
  review.error = undefined;
  review.loading = false;
  overviewRefresh.run = () => {};
  reviewRefresh.run = async () => {};
  reviewCalls.args = [];
  overviewSockets.length = 0;
  pageState.shallow = null;
});

afterEach(() => {
  vi.useRealTimers();
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

  it("offers the wall display in the Now lens only", async () => {
    const people = crowd(5);
    overview.data = { tenants: people };
    review.data = reviewOf(people);
    render(PeoplePage);

    await expect
      .element(page.getByRole("link", { name: "Wall display" }))
      .toHaveAttribute("href", "/tenants/wall");

    await page.getByRole("radio", { name: "Review" }).click();
    await expect.element(page.getByText("Distribution")).toBeVisible();
    expect(page.getByRole("link", { name: "Wall display" }).elements()).toHaveLength(0);

    await page.getByRole("radio", { name: "Now" }).click();
    await expect.element(page.getByRole("link", { name: "Wall display" })).toBeVisible();
  });

  it("draws no reading as current once the overview read has failed", async () => {
    overview.data = { tenants: crowd(5) };
    render(PeoplePage);
    await expect.element(page.getByText("Reading age")).toBeVisible();
    expect(document.body.innerHTML).toContain("bg-glucose-in-range");

    overview.error = new Error("The read failed.");

    await expect.element(page.getByText("The list of people did not load.")).toBeVisible();
    expect(document.body.innerHTML).not.toContain("bg-glucose-in-range");
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
    await expect.poll(() => nav.address.search).toBe("?lens=review&days=14");

    await page.getByRole("radio", { name: "90 days" }).click();
    await expect.poll(() => nav.address.search).toBe("?lens=review&days=90");

    await page.getByRole("radio", { name: "Now" }).click();
    await expect.poll(() => nav.address.search).toBe("");
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

  it("follows the address a shallow rewrite shows when Back returns to the page", async () => {
    const people = crowd(5);
    overview.data = { tenants: people };
    review.data = reviewOf(people);
    openAt("");
    await expect.element(page.getByText("Reading age")).toBeVisible();

    pageState.url = new URL("http://localhost/");
    Object.assign(pageState, {
      shallow: { url: new URL("http://localhost/?lens=review&days=30&person=person1") },
    });
    nav.afterNavigate();

    await expect.element(page.getByTestId("person-panel")).toBeVisible();
    expect(checked("30 days")).toBe("true");
    expect(page.getByTestId("person-row").elements().find((row) => row.getAttribute("aria-current") === "true")?.getAttribute("data-person")).toBe("person1");
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

describe("PeoplePage address", () => {
  const people = crowd(5);

  function setUp() {
    overview.data = { tenants: people };
    review.data = reviewOf(people);
  }

  function hrefs(search: (slug: string) => string) {
    return people.map((p) => `/${search(p.slug!)}`);
  }

  it("states what the page shows after each step of Review, Now, select, close", async () => {
    setUp();
    openAt("");
    await expect.element(page.getByText("Reading age")).toBeVisible();

    await page.getByRole("radio", { name: "Review" }).click();
    await expect.poll(() => nav.address.search).toBe("?lens=review&days=14");
    expect(rowHrefs()).toEqual(hrefs((slug) => `?lens=review&days=14&person=${slug}`));

    await page.getByRole("radio", { name: "Now" }).click();
    await expect.poll(() => nav.address.search).toBe("");
    expect(rowHrefs()).toEqual(hrefs((slug) => `?person=${slug}`));

    await page.getByTestId("person-row").nth(1).click();
    await expect.element(page.getByTestId("person-panel")).toBeVisible();
    expect(nav.address.search).toBe("?person=person1");
    expect(checked("Now")).toBe("true");
    expect(rowHrefs()).toEqual(hrefs((slug) => `?person=${slug}`));

    await page.getByRole("button", { name: "Close" }).click();
    await expect.poll(() => page.getByTestId("person-panel").elements().length).toBe(0);
    expect(nav.address.search).toBe("");
    expect(checked("Now")).toBe("true");
    expect(rowHrefs()).toEqual(hrefs((slug) => `?person=${slug}`));
  });

  it("keeps the lens and the period through Now, Review with 30 days, select", async () => {
    setUp();
    openAt("");
    await expect.element(page.getByText("Reading age")).toBeVisible();

    await page.getByRole("radio", { name: "Review" }).click();
    await page.getByRole("radio", { name: "30 days" }).click();
    await expect.poll(() => nav.address.search).toBe("?lens=review&days=30");
    expect(rowHrefs()).toEqual(hrefs((slug) => `?lens=review&days=30&person=${slug}`));

    await page.getByTestId("person-row").nth(2).click();
    await expect.element(page.getByTestId("person-panel")).toBeVisible();
    expect(nav.address.search).toBe("?lens=review&days=30&person=person2");
    expect(checked("30 days")).toBe("true");

    await page.getByRole("radio", { name: "Now" }).click();
    await expect.poll(() => nav.address.search).toBe("?person=person2");
    await page.getByRole("radio", { name: "Review" }).click();
    await expect.poll(() => nav.address.search).toBe("?lens=review&days=30&person=person2");
  });

  it("drops a person who leaves the list from the address and keeps the lens", async () => {
    setUp();
    openAt("");
    await page.getByRole("radio", { name: "Review" }).click();
    await page.getByTestId("person-row").nth(1).click();
    await expect.element(page.getByTestId("person-panel")).toBeVisible();
    await page.getByRole("radio", { name: "Now" }).click();
    await expect.poll(() => nav.address.search).toBe("?person=person1");

    overview.data = { tenants: people.filter((p) => p.slug !== "person1") };

    await expect.poll(() => nav.address.search).toBe("");
    expect(checked("Now")).toBe("true");
  });

  it.each([
    ["", "Now", null],
    ["?person=person1", "Now", "person1"],
    ["?lens=review&days=14", "14 days", null],
    ["?lens=review&days=30&person=person1", "30 days", "person1"],
  ])("shows the lens, period and person that %s names on a reload", async (search, control, person) => {
    setUp();
    openAt(search);

    await expect.element(page.getByTestId("person-row").first()).toBeVisible();
    expect(checked(control)).toBe("true");
    const open = page
      .getByTestId("person-row")
      .elements()
      .filter((row) => row.getAttribute("aria-current") === "true")
      .map((row) => row.getAttribute("data-person"));
    expect(open).toEqual(person ? [person] : []);
    expect(page.getByTestId("person-panel").elements().length).toBe(person ? 1 : 0);
  });
});

describe("PeoplePage review read for people the response lacks", () => {
  const people = crowd(5);

  function countReads() {
    const reads = { count: 0, release: () => {} };
    reviewRefresh.run = async () => {
      reads.count += 1;
    };
    return reads;
  }

  it("reads once more, shows the placeholder meanwhile, then shows the figures", async () => {
    overview.data = { tenants: people };
    review.data = reviewOf(people.slice(1));
    const reads = countReads();
    const gate = new Promise<void>((resolve) => (reads.release = resolve));
    reviewRefresh.run = async () => {
      reads.count += 1;
      await gate;
      review.data = reviewOf(people);
    };
    openAt("?lens=review&days=14");

    await expect.element(page.getByTestId("review-loading")).toBeVisible();
    expect(page.getByTestId("review-loading").elements()).toHaveLength(1);
    expect(page.getByText("The statistics did not load.").elements()).toHaveLength(0);
    expect(reads.count).toBe(1);

    reads.release();

    await expect.poll(() => page.getByTestId("review-gmi").elements().length).toBe(5);
    expect(page.getByTestId("review-loading").elements()).toHaveLength(0);
    expect(page.getByText("The statistics did not load.").elements()).toHaveLength(0);
    expect(reads.count).toBe(1);
  });

  it("states that the statistics did not load for a person still absent, and reads no more", async () => {
    overview.data = { tenants: people };
    review.data = reviewOf(people.slice(1));
    const reads = countReads();
    openAt("?lens=review&days=14");

    await expect.element(page.getByTestId("review-notice")).toHaveTextContent("The statistics did not load.");
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(page.getByTestId("review-loading").elements()).toHaveLength(0);
    expect(page.getByTestId("review-notice").elements()).toHaveLength(1);
    expect(reads.count).toBe(1);
  });

  it("reads once for a person who joins the list after the review was read", async () => {
    const joined = [...people, makePerson({ slug: "person5" })];
    overview.data = { tenants: people };
    review.data = reviewOf(people);
    const reads = countReads();
    openAt("?lens=review&days=14");
    await expect.element(page.getByTestId("review-gmi").first()).toBeVisible();
    expect(reads.count).toBe(0);

    overview.data = { tenants: joined };

    await expect.element(page.getByTestId("review-notice")).toHaveTextContent("The statistics did not load.");
    expect(reads.count).toBe(1);
  });

  it("states that the statistics did not load for a person the API marked failed, with no extra read", async () => {
    overview.data = { tenants: people };
    review.data = makeReview(
      people.map((p, i) =>
        makeReviewEntry(
          p,
          i === 0 ? { unavailable: TenantReviewUnavailable.Failed, statistics: undefined } : {}
        )
      )
    );
    const reads = countReads();
    openAt("?lens=review&days=14");

    await expect.element(page.getByTestId("review-notice")).toHaveTextContent("The statistics did not load.");
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(page.getByTestId("review-notice").elements()).toHaveLength(1);
    expect(reads.count).toBe(0);
  });

  function gatedReads() {
    const reads = { count: 0, gates: [] as Array<() => void> };
    reviewRefresh.run = () => {
      reads.count += 1;
      return new Promise<void>((resolve) => reads.gates.push(resolve));
    };
    return reads;
  }

  const settle = () => new Promise((resolve) => setTimeout(resolve, 100));

  it("keeps the placeholder for a person whose read is in flight when an earlier read ends", async () => {
    const joined = [...people, makePerson({ slug: "person5" })];
    overview.data = { tenants: people };
    review.data = reviewOf(people.slice(0, 4));
    const reads = gatedReads();
    openAt("?lens=review&days=14");
    await expect.element(page.getByTestId("review-loading")).toBeVisible();

    overview.data = { tenants: joined };
    await expect.poll(() => reads.count).toBe(2);
    await expect.poll(() => page.getByTestId("review-loading").elements().length).toBe(2);

    reads.gates[0]();
    await settle();

    expect(page.getByTestId("review-loading").elements()).toHaveLength(2);
    expect(page.getByText("The statistics did not load.").elements()).toHaveLength(0);

    reads.gates[1]();

    await expect.poll(() => page.getByTestId("review-notice").elements().length).toBe(2);
    expect(page.getByTestId("review-loading").elements()).toHaveLength(0);
    expect(reads.count).toBe(2);
  });

  it("keeps the placeholder under a new period while the earlier period's read ends", async () => {
    overview.data = { tenants: people };
    review.data = reviewOf(people.slice(1));
    const reads = gatedReads();
    openAt("?lens=review&days=14");
    await expect.element(page.getByTestId("review-loading")).toBeVisible();

    await page.getByRole("radio", { name: "30 days" }).click();
    await expect.poll(() => reads.count).toBe(2);

    reads.gates[0]();
    await settle();

    expect(page.getByTestId("review-loading").elements()).toHaveLength(1);
    expect(page.getByText("The statistics did not load.").elements()).toHaveLength(0);
  });

  it("reads once for each person: not again for a reorder, a smaller list, or a period shown again", async () => {
    const joined = [...people, makePerson({ slug: "person5" })];
    overview.data = { tenants: joined };
    review.data = reviewOf(people.slice(0, 4));
    const reads = gatedReads();
    openAt("?lens=review&days=14");
    await expect.poll(() => reads.count).toBe(1);
    reads.gates[0]();
    await expect.element(page.getByTestId("review-notice").first()).toBeVisible();
    expect(page.getByTestId("review-notice").elements()).toHaveLength(2);

    overview.data = { tenants: [...joined].reverse() };
    await settle();
    overview.data = { tenants: joined.slice(0, 5) };
    await settle();
    expect(reads.count).toBe(1);

    overview.data = { tenants: joined };
    await page.getByRole("radio", { name: "30 days" }).click();
    await expect.poll(() => reads.count).toBe(2);
    await page.getByRole("radio", { name: "14 days" }).click();
    await settle();

    expect(reads.count).toBe(2);
  });

  it("does not read the review on the overview poll tick or on a ping", async () => {
    overview.data = { tenants: people };
    review.data = reviewOf(people);
    const reads = countReads();
    openAt("?lens=review&days=14");
    await expect.element(page.getByTestId("review-gmi").first()).toBeVisible();
    const callsBefore = reviewCalls.args.length;

    vi.useFakeTimers();
    poll.tick();
    overviewSockets[0].handlers.overviewUpdate?.();
    await vi.advanceTimersByTimeAsync(OVERVIEW_PING_DEBOUNCE_MS);

    expect(reads.count).toBe(0);
    expect(reviewCalls.args).toHaveLength(callsBefore);
  });
});
