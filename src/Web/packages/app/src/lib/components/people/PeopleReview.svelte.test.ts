import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it } from "vitest";
import {
  GlucoseStatus,
  NotificationUrgency,
  TenantOverviewAttentionKind,
  TenantReviewUnavailable,
  type TenantOverviewItem,
  type TenantReviewPerson,
} from "#lib/api/generated/nocturne-api-client.js";
import PeopleRoster from "./PeopleRoster.svelte";
import {
  makePerson,
  makeReview,
  makeReviewEntry,
  makeStatistics,
} from "./people-fixtures.js";
import type { ReviewDays, ReviewState } from "./review-lens.js";

function crowd(count: number): TenantOverviewItem[] {
  return Array.from({ length: count }, (_, i) =>
    makePerson({ slug: `person${i}` })
  );
}

function ready(
  entries: TenantReviewPerson[],
  days: ReviewDays = 14
): ReviewState {
  return { status: "ready", response: makeReview(entries), days };
}

function renderReview(people: TenantOverviewItem[], review: ReviewState) {
  return render(PeopleRoster, {
    props: { people, review },
  });
}

function rowOf(slug: string) {
  const row = page
    .getByTestId("person-row")
    .elements()
    .find((link) => link.querySelector("p")?.textContent?.trim() === slug);
  if (!row) throw new Error(`no row for ${slug}`);
  return row;
}

function linkedNames() {
  return page
    .getByTestId("person-row")
    .elements()
    .map((link) => link.querySelector("p")?.textContent?.trim());
}

beforeEach(async () => {
  await page.viewport(1100, 900);
});

describe("Review rows", () => {
  it("shows each person's figures with their percent signs", async () => {
    const people = crowd(5);
    renderReview(people, ready(people.map((p) => makeReviewEntry(p))));

    await expect.element(page.getByTestId("review-gmi").first()).toBeVisible();
    const row = rowOf("Person0");
    expect(row.querySelector('[data-testid="review-gmi"]')?.textContent?.trim()).toBe("7.1%");
    expect(row.querySelector('[data-testid="review-cv"]')?.textContent?.trim()).toBe("34%");
    expect(row.querySelector('[data-testid="review-wear"]')?.textContent?.replace(/\s+/g, " ").trim()).toBe("Wear 91%");
    expect(row.textContent).toContain("62%");
  });

  it("shows no figure under the distribution bar that has no label", async () => {
    const people = crowd(5);
    renderReview(people, ready(people.map((p) => makeReviewEntry(p))));

    const distribution = page.getByTestId("review-distribution").first();
    await expect.element(distribution).toBeVisible();
    expect(distribution.element().textContent).not.toMatch(/\d/);
  });

  it("states an empty figure in words for a screen reader", async () => {
    const people = crowd(5);
    renderReview(
      people,
      ready(
        people.map((p) =>
          makeReviewEntry(p, {
            statistics: makeStatistics({ gmi: undefined, cgmActivePercent: undefined }),
          })
        )
      )
    );

    await expect.element(page.getByTestId("review-cv").first()).toBeVisible();
    const row = rowOf("Person0");
    for (const id of ["review-gmi", "review-wear"]) {
      expect(row.querySelector(`[data-testid="${id}"] [aria-label="No figure"]`)).not.toBeNull();
    }
  });

  it("labels the time in range and the wear in words only where the column labels are hidden", async () => {
    const people = crowd(5);
    renderReview(people, ready(people.map((p) => makeReviewEntry(p))));
    await expect.element(page.getByTestId("review-gmi").first()).toBeVisible();
    const row = rowOf("Person0");
    const words = [...row.querySelectorAll("span")].filter((el) =>
      ["in range", "Wear"].includes(el.textContent?.trim() ?? "")
    );
    expect(words).toHaveLength(2);
    for (const word of words) {
      expect(word.getBoundingClientRect().width).toBeLessThanOrEqual(1);
    }

    await page.viewport(390, 900);
    await expect.poll(() => words.every((word) => word.getBoundingClientRect().width > 8)).toBe(true);
  });

  it("labels the columns", async () => {
    const people = crowd(5);
    renderReview(people, ready(people.map((p) => makeReviewEntry(p))));

    const labels = page.getByTestId("people-column-labels");
    await expect.element(labels).toBeVisible();
    for (const label of ["Person", "Time in range", "Change", "Distribution", "GMI", "CV", "Wear"]) {
      await expect.element(labels).toHaveTextContent(label);
    }
    expect(labels.element().textContent).not.toContain("Reading age");
  });

  it("keeps the people, the sections and their order the same as in the Now lens", async () => {
    const people = [
      makePerson({ slug: "zoe" }),
      makePerson({
        slug: "amy",
        status: GlucoseStatus.Low,
        attention: [
          {
            kind: TenantOverviewAttentionKind.GlucoseOutOfRange,
            level: NotificationUrgency.Urgent,
          },
        ],
      }),
      ...crowd(4),
    ];
    const now = render(PeopleRoster, {
      props: { people },
    });
    await expect.element(page.getByText("Zoe")).toBeVisible();
    const nowNames = linkedNames();
    const nowHeadings = page.getByRole("heading").elements().map((h) => h.textContent?.replace(/\s+/g, " ").trim());
    await now.unmount();

    renderReview(people, ready(people.map((p) => makeReviewEntry(p))));
    await expect.element(page.getByText("Zoe")).toBeVisible();

    expect(linkedNames()).toEqual(nowNames);
    expect(
      page.getByRole("heading").elements().map((h) => h.textContent?.replace(/\s+/g, " ").trim())
    ).toEqual(nowHeadings);
    expect(nowHeadings).toEqual(["Flagged 1", "Other people 5"]);
  });

  it("joins the entries to the people on tenant id, not on position", async () => {
    const people = crowd(5);
    const entries = people
      .map((p, i) =>
        makeReviewEntry(p, {
          statistics: makeStatistics({ gmi: 6 + i }),
        })
      )
      .reverse();
    renderReview(people, ready(entries));

    await expect.element(page.getByTestId("review-gmi").first()).toBeVisible();
    expect(rowOf("Person0").querySelector('[data-testid="review-gmi"]')?.textContent?.trim()).toBe("6.0%");
    expect(rowOf("Person4").querySelector('[data-testid="review-gmi"]')?.textContent?.trim()).toBe("10.0%");
  });
});

describe("Review change", () => {
  async function changeOf(change: number | undefined) {
    const people = crowd(5);
    renderReview(
      people,
      ready(
        people.map((p) =>
          makeReviewEntry(p, {
            statistics: makeStatistics({ timeInRangeChange: change }),
          })
        )
      )
    );
    await expect.element(page.getByTestId("review-gmi").first()).toBeVisible();
    return rowOf("Person0");
  }

  it("shows an up arrow, the size and a label for a rise", async () => {
    const row = await changeOf(4);

    const change = row.querySelector('[data-testid="review-change"]')!;
    expect(change.getAttribute("aria-label")).toBe("Up 4 points from the previous 14 days");
    expect(change.textContent?.trim()).toBe("4");
    expect(change.querySelector(".lucide-arrow-up")).not.toBeNull();
    expect(change.querySelector(".lucide-arrow-down")).toBeNull();
  });

  it("shows a down arrow, the size and a label for a fall", async () => {
    const row = await changeOf(-12);

    const change = row.querySelector('[data-testid="review-change"]')!;
    expect(change.getAttribute("aria-label")).toBe("Down 12 points from the previous 14 days");
    expect(change.textContent?.trim()).toBe("12");
    expect(change.querySelector(".lucide-arrow-down")).not.toBeNull();
    expect(change.querySelector(".lucide-arrow-up")).toBeNull();
  });

  it("shows 0 and no arrow for no change", async () => {
    const row = await changeOf(0);

    const change = row.querySelector('[data-testid="review-change"]')!;
    expect(change.textContent?.trim()).toBe("0");
    expect(change.querySelector("svg")).toBeNull();
  });

  it.each([
    [0.4, "Up less than 1 point from the previous 14 days", "up"],
    [-0.4, "Down less than 1 point from the previous 14 days", "down"],
  ])("does not call a change of %f no change", async (value, label, direction) => {
    const row = await changeOf(value);

    const change = row.querySelector('[data-testid="review-change"]')!;
    expect(change.getAttribute("aria-label")).toBe(label);
    expect(change.textContent?.trim()).toBe("<1");
    expect(change.querySelector(`.lucide-arrow-${direction}`)).not.toBeNull();
  });

  it("shows nothing for a change the API does not give", async () => {
    const row = await changeOf(undefined);

    expect(row.querySelector('[data-testid="review-change"]')).toBeNull();
  });

  it("names the period it was read for", async () => {
    const people = crowd(5);
    renderReview(people, ready(people.map((p) => makeReviewEntry(p)), 90));

    await expect.element(page.getByTestId("review-gmi").first()).toBeVisible();
    expect(
      rowOf("Person0").querySelector('[data-testid="review-change"]')?.getAttribute("aria-label")
    ).toBe("Up 4 points from the previous 90 days");
  });
});

describe("Review states", () => {
  function renderStates(entry: Partial<TenantReviewPerson>) {
    const people = crowd(5);
    renderReview(
      people,
      ready(
        people.map((p, i) =>
          makeReviewEntry(p, i === 0 ? entry : {})
        )
      )
    );
  }

  it("states a history limit with a lock", async () => {
    renderStates({
      unavailable: TenantReviewUnavailable.HistoryLimited,
      statistics: undefined,
    });

    const notice = page.getByTestId("review-notice");
    await expect
      .element(notice)
      .toHaveTextContent("24 hours only. Statistics for a period are not available.");
    expect(notice.element().querySelector(".lucide-lock")).not.toBeNull();
    expect(rowOf("Person0").querySelector('[data-testid="review-gmi"]')).toBeNull();
    expect(page.getByTestId("review-gmi").elements()).toHaveLength(4);
  });

  it("states missing report access with a lock", async () => {
    renderStates({
      unavailable: TenantReviewUnavailable.NoReportAccess,
      statistics: undefined,
    });

    const notice = page.getByTestId("review-notice");
    await expect
      .element(notice)
      .toHaveTextContent("You do not have access to this person's reports.");
    expect(notice.element().querySelector(".lucide-lock")).not.toBeNull();
  });

  it("states a failed build with no lock", async () => {
    renderStates({
      unavailable: TenantReviewUnavailable.Failed,
      statistics: undefined,
    });

    const notice = page.getByTestId("review-notice");
    await expect.element(notice).toHaveTextContent("The statistics did not load.");
    expect(notice.element().querySelector(".lucide-lock")).toBeNull();
  });

  it("states that the statistics did not load for a person with no entry", async () => {
    const people = crowd(5);
    renderReview(
      people,
      ready(people.slice(1).map((p) => makeReviewEntry(p)))
    );

    const notice = page.getByTestId("review-notice");
    await expect.element(notice).toHaveTextContent("The statistics did not load.");
    expect(rowOf("Person0").querySelector('[data-testid="review-notice"]')).not.toBeNull();
    expect(page.getByTestId("review-notice").elements()).toHaveLength(1);
  });

  it("states a period with no readings", async () => {
    renderStates({
      statistics: {
        timeInRange: undefined,
        gmi: undefined,
        coefficientOfVariation: undefined,
        cgmActivePercent: undefined,
        timeInRangeChange: undefined,
        reliability: { meetsReliabilityCriteria: false, daysOfData: 0 },
      },
    });

    await expect
      .element(page.getByTestId("review-notice"))
      .toHaveTextContent("No readings in this period.");
    expect(page.getByTestId("review-wear").elements()).toHaveLength(4);
  });

  it("shows a placeholder, not zeros, while the read is loading", async () => {
    const people = crowd(5);
    renderReview(people, { status: "loading" });

    await expect.element(page.getByTestId("review-loading").first()).toBeVisible();
    expect(page.getByTestId("review-loading").elements()).toHaveLength(5);
    expect(page.getByTestId("people-column-labels").element().textContent).toContain("Time in range");
    expect(document.body.textContent).not.toContain("0%");
  });

  it("keeps the person cells and adds nothing to the rows when the read failed", async () => {
    const people = crowd(5);
    renderReview(people, { status: "failed" });

    await expect.element(page.getByText("Person0")).toBeVisible();
    expect(linkedNames()).toEqual(people.map((p) => p.displayName));
    expect(page.getByTestId("review-notice").elements()).toHaveLength(0);
    expect(page.getByTestId("review-loading").elements()).toHaveLength(0);
  });
});

describe("Review reliability", () => {
  function renderMixed() {
    const people = crowd(5);
    renderReview(
      people,
      ready(
        people.map((p, i) =>
          makeReviewEntry(p, {
            statistics: makeStatistics(
              i === 0
                ? { reliability: { meetsReliabilityCriteria: false, daysOfData: 3 } }
                : {}
            ),
          })
        )
      )
    );
  }

  function hatchOf(bar: Element) {
    return getComputedStyle(bar.querySelector("[data-slot=progress-indicator]")!).backgroundImage;
  }

  it("hatches the bar and marks the time in range, not the wear figure, when the period is not reliable", async () => {
    renderMixed();

    await expect.element(page.getByTestId("review-wear").first()).toBeVisible();
    const row = rowOf("Person0");
    const bar = row.querySelector('[data-testid="review-bar"]')!;
    const wear = row.querySelector('[data-testid="review-wear"]')!;
    const limited = row.querySelector('[data-testid="review-limited"]')!;
    expect(bar.hasAttribute("data-hatched")).toBe(true);
    expect(hatchOf(bar)).toContain("repeating-linear-gradient");
    expect(limited.textContent).toContain("Limited data");
    expect(limited.className).toContain("text-severity-warn");
    expect(limited.querySelector(".lucide-triangle-alert")).not.toBeNull();
    expect(
      limited.compareDocumentPosition(row.querySelector('[data-testid="review-change"]')!) &
        Node.DOCUMENT_POSITION_FOLLOWING
    ).toBeTruthy();
    expect(wear.textContent).not.toContain("Limited data");
    expect(wear.className).not.toContain("text-severity-warn");
    expect(wear.querySelector("svg")).toBeNull();
  });

  it("leaves a reliable period solid and unmarked", async () => {
    renderMixed();

    await expect.element(page.getByTestId("review-wear").first()).toBeVisible();
    const row = rowOf("Person1");
    const bar = row.querySelector('[data-testid="review-bar"]')!;
    expect(bar.hasAttribute("data-hatched")).toBe(false);
    expect(hatchOf(bar)).toBe("none");
    expect(row.querySelector('[data-testid="review-limited"]')).toBeNull();
  });

  it("draws the bar to the in-range percent", async () => {
    renderMixed();

    await expect.element(page.getByTestId("review-bar").first()).toBeVisible();
    const bar = rowOf("Person1").querySelector('[data-testid="review-bar"]')!;
    expect(bar.getAttribute("aria-valuenow")).toBe("62");
    expect(bar.getAttribute("aria-label")).toBe("Time in range");
  });
});

describe("Review in the large layout", () => {
  it("shows the lead figure, the bar, the sentence, the distribution and the figure strip at 4 people", async () => {
    const people = crowd(4);
    renderReview(people, ready(people.map((p) => makeReviewEntry(p))));

    await expect.element(page.getByText("Person0")).toBeVisible();
    expect(page.getByTestId("people-column-labels").elements()).toHaveLength(0);
    expect(page.getByTestId("review-bar").elements()).toHaveLength(4);
    expect(page.getByTestId("review-distribution").elements()).toHaveLength(4);
    const row = rowOf("Person0");
    expect(row.textContent).toContain("62%");
    expect(row.textContent).toContain("in range");
    expect(
      row.querySelector('[data-testid="review-change-sentence"]')?.textContent?.trim()
    ).toBe("Up 4 points from the previous 14 days.");
    const strip = row.querySelector("dl")!;
    expect(strip.textContent).toContain("GMI");
    expect(strip.textContent).toContain("7.1%");
    expect(strip.textContent).toContain("CV");
    expect(strip.textContent).toContain("34%");
    expect(strip.textContent).toContain("Sensor wear");
    expect(strip.textContent).toContain("91%");
  });

  it("marks the sensor wear figure when the period is not reliable", async () => {
    const people = crowd(4);
    renderReview(
      people,
      ready(
        people.map((p) =>
          makeReviewEntry(p, {
            statistics: makeStatistics({
              reliability: { meetsReliabilityCriteria: false, daysOfData: 2 },
            }),
          })
        )
      )
    );

    await expect.element(page.getByText("Person0")).toBeVisible();
    const row = rowOf("Person0");
    const strip = row.querySelector("dl")!;
    const limited = row.querySelector('[data-testid="review-limited"]')!;
    expect(limited.textContent).toContain("Limited data");
    expect(limited.querySelector(".lucide-triangle-alert")).not.toBeNull();
    expect(strip.textContent).not.toContain("Limited data");
    expect(strip.querySelector("svg")).toBeNull();
    expect(
      row.querySelector('[data-testid="review-bar"]')!.compareDocumentPosition(limited) &
        Node.DOCUMENT_POSITION_PRECEDING
    ).toBeTruthy();
    expect(
      rowOf("Person0").querySelector('[data-testid="review-bar"]')?.hasAttribute("data-hatched")
    ).toBe(true);
  });

  it("states a history limit in place of the figures", async () => {
    const people = crowd(4);
    renderReview(
      people,
      ready(
        people.map((p, i) =>
          makeReviewEntry(
            p,
            i === 0
              ? {
                  unavailable: TenantReviewUnavailable.HistoryLimited,
                  statistics: undefined,
                }
              : {}
          )
        )
      )
    );

    await expect
      .element(page.getByTestId("review-notice"))
      .toHaveTextContent("24 hours only. Statistics for a period are not available.");
    expect(rowOf("Person0").querySelector("dl")).toBeNull();
    expect(rowOf("Person1").querySelector("dl")).not.toBeNull();
  });

  it("states a period with no readings in place of the figures", async () => {
    const people = crowd(4);
    renderReview(
      people,
      ready(
        people.map((p, i) =>
          makeReviewEntry(
            p,
            i === 0
              ? {
                  statistics: {
                    timeInRange: undefined,
                    gmi: undefined,
                    coefficientOfVariation: undefined,
                    cgmActivePercent: undefined,
                    timeInRangeChange: undefined,
                  },
                }
              : {}
          )
        )
      )
    );

    await expect
      .element(page.getByTestId("review-notice"))
      .toHaveTextContent("No readings in this period.");
    expect(rowOf("Person0").querySelector("dl")).toBeNull();
  });
});

describe("Review row at a narrow width", () => {
  it.each([320, 375])(
    "keeps the two-line row inside its box at %ipx, with the time in range, the change and the wear and nothing else",
    async (width) => {
      await page.viewport(width, 900);
      const people = crowd(5);
      people[0] = makePerson({
        slug: "person0",
        displayName: "Alexandria Montgomery",
      });
      renderReview(
        people,
        ready(
          people.map((p) =>
            makeReviewEntry(p, {
              statistics: makeStatistics({
                timeInRangeChange: -12,
                reliability: { meetsReliabilityCriteria: false, daysOfData: 2 },
              }),
            })
          )
        )
      );

      const links = page.getByTestId("person-row");
      await expect.element(links.first()).toBeVisible();
      for (const link of links.elements()) {
        expect(link.scrollWidth).toBeLessThanOrEqual(link.clientWidth);
        const name = link.querySelector("p")!;
        expect(name.scrollWidth).toBeLessThanOrEqual(name.clientWidth);
        const second = [
          link.querySelector('[data-testid="review-bar"]')!,
          link.querySelector('[data-testid="review-change"]')!,
          link.querySelector('[data-testid="review-wear"]')!,
          link.querySelector('[data-testid="review-limited"]')!,
        ];
        const labels = [...link.querySelectorAll("span")].filter((el) =>
          ["in range", "Wear"].includes(el.textContent?.trim() ?? "")
        );
        expect(labels.map((el) => el.textContent?.trim()).sort()).toEqual(["Wear", "in range"]);
        for (const label of labels) {
          expect(label.getBoundingClientRect().width).toBeGreaterThan(8);
        }
        second.push(...labels);
        const nameBottom = name.getBoundingClientRect().bottom;
        const tops = second.map((el) => el.getBoundingClientRect().top);
        for (const el of second) {
          const box = el.getBoundingClientRect();
          expect(box.top).toBeGreaterThanOrEqual(nameBottom);
          expect(box.right).toBeLessThanOrEqual(link.getBoundingClientRect().right);
        }
        expect(Math.max(...tops) - Math.min(...tops)).toBeLessThan(second[0].getBoundingClientRect().height + 16);
      }
      for (const hidden of ["review-gmi", "review-cv", "review-distribution"]) {
        for (const el of page.getByTestId(hidden).elements()) {
          expect(el.getBoundingClientRect().width).toBe(0);
        }
      }
      expect(page.getByTestId("people-column-labels").element().getBoundingClientRect().height).toBe(0);
    }
  );
});
