import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import {
  GlucoseStatus,
  TenantOverviewAttentionKind,
  TenantPersonEntryKind,
  type TenantOverviewResponse,
  type TenantPersonDetail,
} from "@nocturne/api-client";
import { makeDetail, makePerson, minutesAgo } from "./people-fixtures.js";

const overview = $state<{ data: TenantOverviewResponse | undefined }>({
  data: undefined,
});

/** One shared slot, as a late response lands: whichever query it was asked by. */
const response = $state<{ data: TenantPersonDetail | undefined; error: unknown }>({
  data: undefined,
  error: undefined,
});

const reads = vi.hoisted(() => ({
  opened: [] as string[],
  refreshed: [] as string[],
}));

const nav = vi.hoisted(() => ({ address: new URL("http://localhost/") }));

vi.mock("$app/navigation", () => ({
  replaceState: (url: string | URL) => {
    nav.address = new URL(url, nav.address.href);
  },
  afterNavigate: () => {},
}));

vi.mock("#lib/websocket/websocket-client.svelte.js", () => import("./people-socket-fixture.svelte.js"));

vi.mock("#lib/api/generated/myTenants.generated.remote.js", () => ({
  getGroups: () => ({ current: [], error: undefined, loading: false, refresh: async () => {} }),
  getOverview: () => ({
    get current() {
      return overview.data;
    },
    error: undefined,
    loading: false,
    refresh: async () => {},
  }),
  getReview: () => ({
    current: undefined,
    error: undefined,
    loading: true,
    refresh: async () => {},
  }),
  getPerson: ({ tenantId }: { tenantId: string }) => {
    reads.opened.push(tenantId);
    return {
      get current() {
        return response.data;
      },
      get error() {
        return response.error;
      },
      get loading() {
        return !response.data;
      },
      refresh: async () => {
        reads.refreshed.push(tenantId);
      },
    };
  },
}));

import PeoplePage from "./PeoplePage.svelte";

const amy = makePerson({ slug: "amy" });
const bob = makePerson({ slug: "bob" });
const others = Array.from({ length: 3 }, (_, i) =>
  makePerson({ slug: `person${i}` })
);

function openAt(search: string, people = [amy, bob, ...others]) {
  overview.data = { tenants: people };
  pageState.url = new URL(`http://localhost/${search}`);
  nav.address = new URL(pageState.url.href);
  return render(PeoplePage);
}

function row(slug: string): HTMLElement {
  const found = page
    .getByTestId("person-row")
    .elements()
    .find((element) => element.getAttribute("data-person") === slug);
  if (!found) throw new Error(`no row for ${slug}`);
  return found as HTMLElement;
}

function panel() {
  return page.getByTestId("person-panel");
}

beforeEach(async () => {
  await page.viewport(1100, 900);
  pageState.data = { baseDomain: "example.com" };
  overview.data = undefined;
  response.data = undefined;
  response.error = undefined;
  reads.opened = [];
  reads.refreshed = [];
});

afterEach(() => {
  vi.useRealTimers();
});

describe("selecting a person", () => {
  it("marks the row and puts the slug in the URL on a click", async () => {
    openAt("");
    response.data = makeDetail(amy);

    await page.getByRole("link", { name: /Amy/ }).first().click();

    expect(row("amy").getAttribute("aria-current")).toBe("true");
    expect(row("bob").getAttribute("aria-current")).toBeNull();
    expect(nav.address.searchParams.get("person")).toBe("amy");
  });

  it("opens from the keyboard", async () => {
    openAt("");
    response.data = makeDetail(amy);

    row("amy").focus();
    await userEvent.keyboard("{Enter}");

    await expect.element(panel()).toBeVisible();
    expect(nav.address.searchParams.get("person")).toBe("amy");
  });

  it("opens the person named in the URL", async () => {
    response.data = makeDetail(bob);
    openAt("?person=bob");

    await expect.element(panel()).toBeVisible();
    expect(row("bob").getAttribute("aria-current")).toBe("true");
  });

  it("opens nothing for a slug that is not in the list, and drops it from the URL", async () => {
    openAt("?person=nobody");

    await expect.element(page.getByText("Person0")).toBeVisible();
    await new Promise((resolve) =>
      requestAnimationFrame(() => requestAnimationFrame(resolve))
    );
    await expect.poll(() => nav.address.searchParams.has("person")).toBe(false);
    expect(panel().elements()).toHaveLength(0);
    expect(reads.opened).toHaveLength(0);
  });

  it("closes the panel, clears the URL and moves focus when the person leaves the list", async () => {
    response.data = makeDetail(amy);
    openAt("?person=amy");
    await expect.element(panel()).toBeVisible();
    page.getByRole("button", { name: "Close" }).element().focus();

    overview.data = { tenants: [bob, ...others] };

    await expect.poll(() => panel().elements().length).toBe(0);
    expect(nav.address.searchParams.has("person")).toBe(false);
    expect(document.activeElement).toBe(
      page.getByRole("heading", { level: 1 }).element()
    );
  });
});

describe("closing the panel", () => {
  beforeEach(() => {
    response.data = makeDetail(amy);
  });

  it("closes on Escape and returns focus to the row", async () => {
    openAt("?person=amy");
    await expect.element(panel()).toBeVisible();

    await userEvent.keyboard("{Escape}");

    await expect.poll(() => panel().elements().length).toBe(0);
    expect(nav.address.searchParams.has("person")).toBe(false);
    expect(document.activeElement).toBe(row("amy"));
  });

  it("closes with the close button and returns focus to the row", async () => {
    openAt("?person=amy");

    await page.getByRole("button", { name: "Close" }).click();

    await expect.poll(() => panel().elements().length).toBe(0);
    expect(document.activeElement).toBe(row("amy"));
  });

  it("closes the sheet on Escape and returns focus to the row", async () => {
    await page.viewport(700, 900);
    openAt("?person=amy");
    await expect.element(panel()).toBeVisible();

    await userEvent.keyboard("{Escape}");

    await expect.poll(() => panel().elements().length).toBe(0);
    expect(document.activeElement).toBe(row("amy"));
  });
});

describe("the URL", () => {
  beforeEach(() => {
    response.data = makeDetail(amy);
  });

  it("keeps the lens and period through opening and closing", async () => {
    openAt("?lens=review&days=30");

    await page.getByRole("link", { name: /Amy/ }).first().click();
    expect(nav.address.searchParams.get("lens")).toBe("review");
    expect(nav.address.searchParams.get("days")).toBe("30");

    await page.getByRole("button", { name: "Close" }).click();
    expect(nav.address.searchParams.get("lens")).toBe("review");
    expect(nav.address.searchParams.get("days")).toBe("30");
  });

  it("keeps the person through a lens change", async () => {
    openAt("?person=amy");
    await expect.element(panel()).toBeVisible();

    await page.getByRole("radio", { name: "Review" }).click();

    expect(nav.address.searchParams.get("person")).toBe("amy");
    expect(nav.address.searchParams.get("lens")).toBe("review");
    await expect.element(panel()).toBeVisible();
  });
});

describe("what the panel shows", () => {
  it("states the current reading with its unit and age", async () => {
    response.data = makeDetail(amy);
    openAt("?person=amy");

    await expect.element(panel().getByTestId("bg-delta")).toHaveTextContent("mg/dL");
    await expect.element(panel().getByTestId("reading-age")).toHaveTextContent("5m ago");
  });

  it("drops the arrow and delta of a stale reading", async () => {
    const stale = makePerson({ slug: "amy", status: GlucoseStatus.Stale });
    response.data = makeDetail(stale);
    openAt("?person=amy", [stale, bob, ...others]);

    await expect.element(panel().getByTestId("reading-age")).toBeVisible();
    expect(panel().getByTestId("bg-delta").elements()).toHaveLength(0);
    expect(panel().getByRole("img", { name: /flat/i }).elements()).toHaveLength(0);
  });

  it("lists the reasons the person is flagged", async () => {
    const flagged = makePerson({
      slug: "amy",
      attention: [{ kind: TenantOverviewAttentionKind.NoRecentReading }],
    });
    response.data = makeDetail(flagged);
    openAt("?person=amy", [flagged, bob, ...others]);

    await expect.element(panel().getByTestId("attention-reasons")).toBeVisible();
  });

  it("draws the 24-hour trace with the unit beside its heading", async () => {
    response.data = makeDetail(amy);
    openAt("?person=amy");

    await expect.element(panel().getByText("Last 24 hours")).toBeVisible();
    await expect.element(panel().getByTestId("person-day-trace")).toBeVisible();
    await expect.element(panel().getByText("mg/dL").first()).toBeVisible();
    expect(
      panel().getByTestId("person-day-trace").element().querySelector("svg")
    ).not.toBeNull();
  });

  it("says there are no readings when the day has none", async () => {
    response.data = makeDetail(amy, { readings: [] });
    openAt("?person=amy");

    await expect
      .element(panel().getByText("No readings in the last 24 hours."))
      .toBeVisible();
    expect(panel().getByTestId("person-day-trace").elements()).toHaveLength(0);
  });

  it("states IOB and omits the values that are unknown", async () => {
    const partial = makePerson({
      slug: "amy",
      therapy: { iob: 1.5, iobKnown: true, cob: 0, cobKnown: false },
      devices: {},
    });
    response.data = makeDetail(partial);
    openAt("?person=amy", [partial, bob, ...others]);

    const line = panel().getByTestId("person-status-line");
    await expect.element(line).toHaveTextContent("IOB");
    await expect.element(line).toHaveTextContent("1.50 U");
    await expect.element(line).not.toHaveTextContent("COB");
    await expect.element(line).not.toHaveTextContent("Reservoir");
  });

  it("lists each entry with its kind, its amount with a unit and its age", async () => {
    response.data = makeDetail(amy);
    openAt("?person=amy");

    const entries = panel().getByTestId("person-entries");
    await expect.element(entries).toBeVisible();
    await expect.element(entries).toHaveTextContent("Bolus");
    await expect.element(entries).toHaveTextContent("4.50 U");
    await expect.element(entries).toHaveTextContent("30m ago");
    await expect.element(entries).toHaveTextContent("Carbs");
    await expect.element(entries).toHaveTextContent("45 g");
    const age = entries.element().querySelector("li span[title]");
    expect(age?.getAttribute("title")).toBeTruthy();
  });

  it("leaves out a missing amount and a kind it does not know", async () => {
    response.data = makeDetail(amy, {
      entries: [
        { kind: TenantPersonEntryKind.Carbs, timestamp: minutesAgo(10) },
        { kind: "Unknown" as TenantPersonEntryKind, timestamp: minutesAgo(20), grams: 9 },
      ],
    });
    openAt("?person=amy");

    const entries = panel().getByTestId("person-entries");
    await expect.element(entries).toHaveTextContent("Carbs");
    await expect.element(entries).not.toHaveTextContent("0 g");
    await expect.element(entries).not.toHaveTextContent("9 g");
    expect(entries.element().querySelectorAll("li")).toHaveLength(1);
  });

  it("says there are no entries when the list is empty", async () => {
    response.data = makeDetail(amy, { entries: [] });
    openAt("?person=amy");

    await expect
      .element(panel().getByText("No boluses or carbs in the last 24 hours."))
      .toBeVisible();
  });

  it("says treatments are not accessible when the entries are null", async () => {
    response.data = makeDetail(amy, { entries: undefined });
    openAt("?person=amy");

    await expect
      .element(
        panel().getByText("You do not have access to this person's treatments.")
      )
      .toBeVisible();
    expect(
      panel().getByText("No boluses or carbs in the last 24 hours.").elements()
    ).toHaveLength(0);
  });
});

describe("a person without an id", () => {
  it("shows the failed state, not a loading line", async () => {
    const nameless = makePerson({ slug: "amy", tenantId: undefined });
    openAt("?person=amy", [nameless, bob, ...others]);

    await expect
      .element(page.getByText("This person's data did not load."))
      .toBeVisible();
    expect(page.getByText("Loading…").elements()).toHaveLength(0);
  });
});

describe("the link to the person's site", () => {
  beforeEach(() => {
    response.data = makeDetail(amy);
  });

  it("points at the person's own site", async () => {
    openAt("?person=amy");

    await expect
      .element(page.getByRole("link", { name: "Open Amy's site" }))
      .toHaveAttribute("href", expect.stringContaining("//amy.example.com"));
  });

  it("is absent without a base domain", async () => {
    pageState.data = { baseDomain: null };
    openAt("?person=amy");

    await expect.element(panel()).toBeVisible();
    expect(
      page.getByRole("link", { name: /Open .* site/ }).elements()
    ).toHaveLength(0);
  });

  it("links each row to this page with the person open", async () => {
    openAt("");

    await expect.element(page.getByText("Person0")).toBeVisible();
    for (const element of page.getByTestId("person-row").elements()) {
      expect(element.getAttribute("href")).toBe(
        `/?person=${element.getAttribute("data-person")}`
      );
    }
  });
});

describe("reading the person", () => {
  it("reads on selection and again for the person selected next", async () => {
    openAt("");
    expect(reads.opened).toHaveLength(0);

    await page.getByRole("link", { name: /Amy/ }).first().click();
    expect(reads.opened).toContain("id-amy");

    await page.getByRole("link", { name: /Bob/ }).first().click();
    expect(reads.opened).toContain("id-bob");
  });

  it("reads again on the minute tick only while the panel is open", async () => {
    vi.useFakeTimers({ toFake: ["setInterval", "clearInterval"] });
    response.data = makeDetail(amy);
    openAt("?person=amy");
    await expect.element(panel()).toBeVisible();

    vi.advanceTimersByTime(60_000);
    expect(reads.refreshed).toEqual(["id-amy"]);

    await page.getByRole("button", { name: "Close" }).click();
    await expect.poll(() => panel().elements().length).toBe(0);
    vi.advanceTimersByTime(180_000);
    expect(reads.refreshed).toEqual(["id-amy"]);
  });

  it("never shows a late response under the person now selected", async () => {
    openAt("");
    await page.getByRole("link", { name: /Amy/ }).first().click();
    await page.getByRole("link", { name: /Bob/ }).first().click();

    response.data = makeDetail(amy, { entries: [] });
    await expect.element(page.getByText("Loading…")).toBeVisible();
    expect(
      panel().getByText("No boluses or carbs in the last 24 hours.").elements()
    ).toHaveLength(0);

    response.data = makeDetail(bob);
    await expect.element(panel().getByTestId("person-entries")).toBeVisible();
  });

  it("shows the name while the read is pending", async () => {
    openAt("?person=amy");

    await expect.element(page.getByText("Loading…")).toBeVisible();
    await expect.element(panel().getByRole("heading", { name: "Amy" })).toBeVisible();
  });

  it("states that the read failed and retries it", async () => {
    response.error = new Error("boom");
    openAt("?person=amy");

    await expect
      .element(page.getByText("This person's data did not load."))
      .toBeVisible();
    await expect.element(panel().getByRole("heading", { name: "Amy" })).toBeVisible();

    await page.getByRole("button", { name: "Try again" }).click();
    expect(reads.refreshed).toEqual(["id-amy"]);
  });
});

describe("where the panel sits", () => {
  beforeEach(() => {
    response.data = makeDetail(amy);
  });

  it("sits beside the list at a wide container, about 24rem wide", async () => {
    openAt("?person=amy");
    await expect.element(panel()).toBeVisible();

    const aside = page.getByRole("complementary").element().getBoundingClientRect();
    const list = row("amy").getBoundingClientRect();
    expect(aside.left).toBeGreaterThanOrEqual(list.right);
    expect(aside.width).toBeGreaterThanOrEqual(380);
    expect(aside.width).toBeLessThanOrEqual(390);
    expect(page.getByRole("dialog").elements()).toHaveLength(0);
  });

  it("names the landmark for the person and clears the sticky banner offset", async () => {
    document.documentElement.style.setProperty("--app-sticky-top", "40px");
    try {
      openAt("?person=amy");

      const aside = page.getByRole("complementary", { name: "Amy" });
      await expect.element(aside).toBeVisible();
      expect(getComputedStyle(aside.element()).top).toBe("56px");
    } finally {
      document.documentElement.style.removeProperty("--app-sticky-top");
    }
  });

  it("compares the content width, not the padded width, with the threshold", async () => {
    document.body.style.width = "910px";
    try {
      openAt("?person=amy");

      await expect.element(page.getByRole("dialog")).toBeVisible();
      expect(page.getByRole("complementary").elements()).toHaveLength(0);
    } finally {
      document.body.style.width = "";
    }
  });

  it("is a sheet over the list below that", async () => {
    await page.viewport(700, 900);
    openAt("?person=amy");

    await expect.element(page.getByRole("dialog")).toBeVisible();
    expect(
      page.getByRole("dialog").element().contains(panel().element())
    ).toBe(true);
    expect(page.getByRole("complementary").elements()).toHaveLength(0);
  });

  it("keeps the list rows inside 320px with the sheet closed", async () => {
    await page.viewport(320, 700);
    openAt("");

    await expect.element(page.getByText("Person0")).toBeVisible();
    for (const element of page.getByTestId("person-row").elements()) {
      expect(element.scrollWidth).toBeLessThanOrEqual(element.clientWidth);
    }
  });
});
