import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { afterEach, beforeEach, describe, it, expect } from "vitest";
import {
  GlucoseDirection,
  GlucoseStatus,
  NotificationUrgency,
  TenantOverviewAttentionKind,
  type TenantOverviewItem,
} from "@nocturne/api-client";
import { glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
import PeopleRoster, { FIRST_ROWS } from "./PeopleRoster.svelte";
import { makePerson, minutesAgo } from "./people-fixtures.js";

const noAccessTitle = "You do not have access to this person's alerts";

function flaggedPerson(slug: string): TenantOverviewItem {
  return makePerson({
    slug,
    status: GlucoseStatus.Low,
    attention: [
      {
        kind: TenantOverviewAttentionKind.GlucoseOutOfRange,
        level: NotificationUrgency.Urgent,
      },
    ],
  });
}

function crowd(count: number, flagged: string[] = []): TenantOverviewItem[] {
  return Array.from({ length: count }, (_, i) => {
    const slug = `person${i}`;
    return flagged.includes(slug) ? flaggedPerson(slug) : makePerson({ slug });
  });
}

function renderRoster(people: TenantOverviewItem[]) {
  return render(PeopleRoster, { props: { people } });
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

describe("PeopleRoster order and sections", () => {
  it("renders people in the order given and never reorders", async () => {
    renderRoster([
      makePerson({ slug: "zoe", status: GlucoseStatus.InRange }),
      makePerson({ slug: "amy", status: GlucoseStatus.UrgentLow }),
      makePerson({ slug: "mia", status: GlucoseStatus.Stale }),
    ]);

    await expect.element(page.getByText("Zoe")).toBeVisible();
    expect(linkedNames()).toEqual(["Zoe", "Amy", "Mia"]);
  });

  it("splits Flagged from Other people on attention, keeping order, with counts", async () => {
    renderRoster(crowd(6, ["person1", "person4"]));

    await expect
      .element(page.getByRole("heading", { name: /Flagged\s*2/ }))
      .toBeVisible();
    await expect
      .element(page.getByRole("heading", { name: /Other people\s*4/ }))
      .toBeVisible();
    expect(linkedNames()).toEqual([
      "Person1",
      "Person4",
      "Person0",
      "Person2",
      "Person3",
      "Person5",
    ]);
  });

  it("renders no heading at all when nobody is flagged", async () => {
    renderRoster(crowd(6));

    await expect.element(page.getByText("Person0")).toBeVisible();
    expect(page.getByRole("heading").elements()).toHaveLength(0);
  });

  it("renders no Other people section when everyone is flagged", async () => {
    renderRoster([flaggedPerson("amy"), flaggedPerson("bob")]);

    await expect
      .element(page.getByRole("heading", { name: /Flagged\s*2/ }))
      .toBeVisible();
    expect(page.getByRole("heading", { name: /Other people/ }).elements())
      .toHaveLength(0);
  });
});

function frame() {
  return new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
}

describe("PeopleRoster reveal", () => {
  it("never mounts fewer rows than before when a poll lands while the rows are still mounting", async () => {
    const people = crowd(200, ["person3", "person150"]);
    const screen = renderRoster(people);
    const counts = [page.getByTestId("person-row").elements().length];
    expect(counts[0]).toBe(FIRST_ROWS);

    for (let poll = 0; counts.at(-1)! < people.length; poll++) {
      if (poll < 6) await screen.rerender({ people: structuredClone(people) });
      await frame();
      counts.push(page.getByTestId("person-row").elements().length);
    }

    expect(counts.slice(1).every((count, i) => count >= counts[i])).toBe(true);
    expect(counts.at(-1)).toBe(people.length);
  });

  it("mounts a step at a time the new people of an answer the same size as the last", async () => {
    const first = crowd(150);
    const screen = renderRoster(first);
    await expect.poll(() => page.getByTestId("person-row").elements().length, { timeout: 30_000 }).toBe(150);

    const second = Array.from({ length: 150 }, (_, i) => makePerson({ slug: `other${i}` }));
    await screen.rerender({ people: second });

    expect(page.getByTestId("person-row").elements().length).toBeLessThan(second.length);
    await expect.poll(() => page.getByTestId("person-row").elements().length, { timeout: 30_000 }).toBe(150);
  });
});

describe("PeopleRoster layout", () => {
  it("uses the large layout at 4 people", async () => {
    renderRoster(crowd(4));

    await expect.element(page.getByText("Person0")).toBeVisible();
    expect(page.getByTestId("person-status-line").elements()).toHaveLength(4);
    expect(page.getByTestId("people-column-labels").elements()).toHaveLength(0);
  });

  it("uses compact rows with column labels at 5 people", async () => {
    renderRoster(crowd(5));

    await expect.element(page.getByTestId("people-column-labels")).toBeVisible();
    expect(page.getByTestId("person-status-line").elements()).toHaveLength(0);
  });

  it("states the glucose unit in the column label", async () => {
    renderRoster(crowd(5));

    await expect
      .element(page.getByTestId("people-column-labels"))
      .toHaveTextContent("Glucose, mg/dL");
  });
});

describe.each([
  ["compact", 5],
  ["large", 1],
])("a person in the %s layout", (_layout, count) => {
  const row = () => page.getByTestId("person-row").first();

  function renderOne(overrides: Partial<TenantOverviewItem>) {
    const people = crowd(count);
    people[0] = makePerson({ slug: "person0", ...overrides });
    return renderRoster(people);
  }

  it("shows value, trend with its label, delta and age for a current reading", async () => {
    renderOne({});

    await expect
      .element(row().getByText("120", { exact: true }))
      .toBeVisible();
    await expect
      .element(row().getByRole("img", { name: "stable" }))
      .toBeVisible();
    await expect.element(row().getByTestId("bg-delta")).toHaveTextContent("+4");
    await expect
      .element(row().getByTestId("reading-age"))
      .toHaveTextContent(/5\s*m/);
  });

  it("drops the range colour and shows the history icon for a stale reading", async () => {
    renderOne({
      status: GlucoseStatus.Stale,
      lastReadingAt: minutesAgo(32),
    });

    const tile = row().getByText("120", { exact: true });
    await expect.element(tile).toBeVisible();
    expect(tile.element().className).toMatch(/bg-muted/);
    expect(tile.element().className).not.toMatch(/bg-glucose-/);
    await expect
      .element(row().getByTestId("age-history-icon"))
      .toBeInTheDocument();
  });

  it("states a stale reading by value and age only, with no arrow and no delta", async () => {
    renderOne({
      status: GlucoseStatus.Stale,
      lastReadingAt: minutesAgo(180),
    });

    await expect.element(row().getByText("120", { exact: true })).toBeVisible();
    expect(row().getByRole("img", { name: "stable" }).elements()).toHaveLength(0);
    expect(row().getByTestId("bg-delta").elements()).toHaveLength(0);
  });

  it("uses the secondary badge for a count with no active-alert reason", async () => {
    renderOne({ activeAlertCount: 2 });

    await expect
      .element(row().getByTestId("alert-badge"))
      .toHaveClass(/bg-secondary/);
  });

  it("colours the tile from the server status for a current reading", async () => {
    renderOne({ status: GlucoseStatus.Low });

    const tile = row().getByText("120", { exact: true });
    await expect.element(tile).toBeVisible();
    expect(tile.element().className).toMatch(/bg-glucose-low/);
    expect(row().getByTestId("age-history-icon").elements()).toHaveLength(0);
  });

  it("shows a dash with no arrow and no delta when there is no reading", async () => {
    renderOne({
      latest: undefined,
      lastReadingAt: undefined,
      status: GlucoseStatus.Unknown,
      recentReadings: [],
    });

    await expect.element(row().getByText("—")).toBeVisible();
    expect(row().getByRole("img", { name: "stable" }).elements()).toHaveLength(0);
    expect(row().getByTestId("bg-delta").elements()).toHaveLength(0);
    await expect
      .element(row().getByTestId("age-history-icon"))
      .toBeInTheDocument();
  });

  it("labels a reading with no trend as unknown, never stable", async () => {
    renderOne({
      latest: { mgdl: 120, delta: 1, direction: GlucoseDirection.None },
    });

    await expect
      .element(row().getByRole("img", { name: "unknown" }))
      .toBeVisible();
    expect(row().getByRole("img", { name: "stable" }).elements()).toHaveLength(0);
  });

  it("shows a lock and no badge when the alert count is null", async () => {
    renderOne({ activeAlertCount: undefined });

    await expect.element(row().getByTitle(noAccessTitle)).toBeVisible();
    expect(row().getByTestId("alert-badge").elements()).toHaveLength(0);
  });

  it("shows the count in a severity badge and no lock when alerts are active", async () => {
    renderOne({
      activeAlertCount: 3,
      attention: [
        {
          kind: TenantOverviewAttentionKind.ActiveAlert,
          level: NotificationUrgency.Hazard,
        },
      ],
    });

    const badge = row().getByTestId("alert-badge");
    await expect.element(badge).toHaveTextContent("3");
    await expect.element(badge).toHaveClass(/bg-severity-hazard/);
    expect(row().getByTitle(noAccessTitle).elements()).toHaveLength(0);
  });

  it("shows neither badge nor lock when there are no active alerts", async () => {
    renderOne({ activeAlertCount: 0 });

    await expect.element(row().getByText("Person0")).toBeVisible();
    expect(row().getByTestId("alert-badge").elements()).toHaveLength(0);
    expect(row().getByTitle(noAccessTitle).elements()).toHaveLength(0);
  });

  it("draws the 3-hour trace only when there are readings", async () => {
    renderOne({});
    await expect.element(row().getByTestId("person-trace")).toBeInTheDocument();
  });

  it("draws no trace without readings", async () => {
    renderOne({ recentReadings: [] });

    await expect.element(row().getByText("Person0")).toBeVisible();
    expect(row().getByTestId("person-trace").elements()).toHaveLength(0);
  });
});

describe("PeopleRoster pump and therapy values", () => {
  it("shows reservoir units and battery in the compact pump cell", async () => {
    const people = crowd(5);
    people[0] = makePerson({
      slug: "person0",
      devices: { reservoirUnits: 120, pumpBatteryPercent: 80 },
    });
    renderRoster(people);

    const cell = page.getByTestId("pump-cell").first();
    await expect.element(cell).toBeInTheDocument();
    expect(cell.element().textContent).toContain("120 U");
    expect(cell.element().textContent).toContain("80%");
  });

  it("leaves the pump cell empty, with no zero, when devices is null", async () => {
    const people = crowd(5);
    people[0] = makePerson({ slug: "person0", devices: undefined });
    renderRoster(people);

    const cell = page.getByTestId("pump-cell").first();
    await expect.element(cell).toBeInTheDocument();
    expect(cell.element().textContent?.trim()).toBe("");
  });

  it("shows IOB, COB and reservoir in the large status line", async () => {
    renderRoster([makePerson({ slug: "amy" })]);

    const line = page.getByTestId("person-status-line");
    await expect.element(line).toBeVisible();
    const text = line.element().textContent ?? "";
    expect(text).toContain("IOB");
    expect(text).toContain("1.50 U");
    expect(text).toContain("COB");
    expect(text).toContain("20 g");
    expect(text).toContain("Reservoir");
    expect(text).toContain("120 U");
  });

  it("omits IOB and COB that are unknown and a reservoir that is null, never showing zero", async () => {
    renderRoster([
      makePerson({
        slug: "amy",
        therapy: { iob: 0, iobKnown: false, cob: 0, cobKnown: false },
        devices: undefined,
      }),
    ]);

    await expect.element(page.getByText("Amy")).toBeVisible();
    expect(page.getByTestId("person-status-line").elements()).toHaveLength(0);
  });

  it("omits the therapy items when therapy is null but keeps the reservoir", async () => {
    renderRoster([makePerson({ slug: "amy", therapy: undefined })]);

    const line = page.getByTestId("person-status-line");
    await expect.element(line).toBeVisible();
    const text = line.element().textContent ?? "";
    expect(text).not.toContain("IOB");
    expect(text).not.toContain("COB");
    expect(text).toContain("Reservoir");
  });

  it("fades the large status line when the reading is stale", async () => {
    renderRoster([makePerson({ slug: "amy", status: GlucoseStatus.Stale })]);

    await expect
      .element(page.getByTestId("person-status-line"))
      .toHaveClass(/opacity-60/);
  });

  it("keeps the unit visible beside a stale value in the large layout", async () => {
    renderRoster([makePerson({ slug: "amy", status: GlucoseStatus.Stale })]);

    const unit = page.getByTestId("bg-unit");
    await expect.element(unit).toHaveTextContent("mg/dL");
    expect(unit.element().className).not.toMatch(/sr-only/);
  });

  it("states the unit once, after the delta, for a current reading in the large layout", async () => {
    renderRoster([makePerson({ slug: "amy" })]);

    await expect.element(page.getByTestId("bg-delta")).toHaveTextContent("mg/dL");
    expect(page.getByTestId("bg-unit").element().className).toMatch(/sr-only/);
  });

  it("does not fade the large status line for a current reading", async () => {
    renderRoster([makePerson({ slug: "amy" })]);

    const line = page.getByTestId("person-status-line");
    await expect.element(line).toBeVisible();
    expect(line.element().className).not.toMatch(/opacity-60/);
  });
});

describe("PeopleRoster reasons", () => {
  it("shows a flagged person's reasons under the name and none for others", async () => {
    renderRoster(crowd(5, ["person1"]));

    await expect.element(page.getByText("Low, steady")).toBeVisible();
    expect(page.getByTestId("attention-reasons").elements()).toHaveLength(1);
  });
});

describe("PeopleRoster in mmol/L", () => {
  beforeEach(() => {
    glucoseUnits.current = "mmol";
  });
  afterEach(() => {
    glucoseUnits.current = "mg/dl";
  });

  it("follows the unit setting in the column label, tile value and delta", async () => {
    const people = crowd(5);
    people[0] = makePerson({
      slug: "person0",
      latest: { mgdl: 99, delta: 18, direction: GlucoseDirection.Flat },
    });
    renderRoster(people);

    await expect
      .element(page.getByTestId("people-column-labels"))
      .toHaveTextContent("Glucose, mmol/L");
    const row = page.getByTestId("person-row").first();
    await expect.element(row.getByText("5.5", { exact: true })).toBeVisible();
    await expect.element(row.getByTestId("bg-delta")).toHaveTextContent("+1.0");
    await expect.element(row.getByTestId("bg-delta")).toHaveTextContent("mmol/L");
  });

  it("sets the unit after the delta in the large layout", async () => {
    renderRoster([makePerson({ slug: "amy" })]);

    await expect
      .element(page.getByTestId("bg-delta"))
      .toHaveTextContent(/\+0\.2\s*mmol\/L/);
  });
});

describe("PeopleRoster pump age", () => {
  it("shows the age of the pump snapshot after the values", async () => {
    const people = crowd(5);
    people[0] = makePerson({
      slug: "person0",
      devices: {
        reservoirUnits: 120,
        pumpBatteryPercent: 85,
        snapshotAt: minutesAgo(120),
      },
    });
    renderRoster(people);

    const cell = page.getByTestId("pump-cell").first();
    await expect.element(cell).toBeInTheDocument();
    expect(cell.element().textContent).toContain("120 U");
    expect(cell.element().textContent).toContain("85%");
    await expect
      .element(page.getByTestId("pump-age").first())
      .toHaveTextContent(/2\s*h/);
  });

  it("shows no pump age without a snapshot time", async () => {
    const people = crowd(5);
    people[0] = makePerson({
      slug: "person0",
      devices: { reservoirUnits: 120, pumpBatteryPercent: 85 },
    });
    renderRoster(people);

    await expect.element(page.getByTestId("pump-cell").first()).toBeInTheDocument();
    expect(page.getByTestId("pump-age").elements()).toHaveLength(0);
  });

  it("shows the pump age beside the reservoir in the large layout", async () => {
    renderRoster([
      makePerson({
        slug: "amy",
        devices: { reservoirUnits: 120, snapshotAt: minutesAgo(120) },
      }),
    ]);

    await expect
      .element(page.getByTestId("person-status-line"))
      .toHaveTextContent(/Reservoir\s*120 U\s*·\s*2\s*h/);
  });
});

describe("PeopleRoster on a narrow container", () => {
  afterEach(async () => {
    await page.viewport(1100, 900);
  });

  it.each([320, 375, 768])("fits every row without overflow or a clipped name at %ipx", async (width) => {
    await page.viewport(width, 900);
    const people = crowd(5);
    people[0] = makePerson({
      slug: "person0",
      displayName: "Alexandria Montgomery",
      activeAlertCount: 3,
    });
    people[1] = makePerson({
      slug: "person1",
      latest: undefined,
      lastReadingAt: undefined,
      status: GlucoseStatus.Unknown,
      recentReadings: [],
    });
    renderRoster(people);

    const links = page.getByTestId("person-row");
    await expect.element(links.first()).toBeVisible();
    for (const link of links.elements()) {
      expect(link.scrollWidth).toBeLessThanOrEqual(link.clientWidth);
      const name = link.querySelector("p")!;
      expect(name.scrollWidth).toBeLessThanOrEqual(name.clientWidth);
    }
    for (const age of page.getByTestId("reading-age").elements()) {
      expect(age.scrollWidth).toBeLessThanOrEqual(age.clientWidth);
    }
  });

  it("hides the column labels and shows the unit beside the value below the wide layout", async () => {
    await page.viewport(375, 900);
    renderRoster(crowd(5));

    await expect.element(page.getByTestId("person-row").first()).toBeVisible();
    expect(page.getByTestId("people-column-labels").element()).not.toBeVisible();
    await expect
      .element(page.getByTestId("person-row").first().getByText("mg/dL").first())
      .toBeVisible();
  });
});

describe("PeopleRoster compact row density", () => {
  it("keeps a row with no reasons, a trace and a pump cell to 48px", async () => {
    renderRoster(crowd(5));

    const link = page.getByTestId("person-row").first();
    await expect.element(link.getByTestId("person-trace")).toBeInTheDocument();
    await expect.element(link.getByTestId("pump-cell")).toBeVisible();
    for (const row of page.getByTestId("person-row").elements()) {
      expect(row.getBoundingClientRect().height).toBeLessThanOrEqual(48);
    }
  });

  it("labels the pump battery for assistive technology and shows its icon", async () => {
    const people = crowd(5);
    people[0] = makePerson({
      slug: "person0",
      devices: { reservoirUnits: 167, pumpBatteryPercent: 21 },
    });
    renderRoster(people);

    const cell = page.getByTestId("pump-cell").first();
    await expect.element(cell).toBeVisible();
    expect(cell.element().textContent).toContain("167 U");
    expect(cell.element().textContent).toContain("Pump battery");
    expect(cell.element().textContent).toContain("21%");
    expect(cell.element().querySelector("svg.lucide-battery")).not.toBeNull();
  });
});
