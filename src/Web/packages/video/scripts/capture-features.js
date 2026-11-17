/**
 * Records what each feature's pages' remote functions return, so a composition can replay the real
 * pages without a stack. Seeds its own sample tenant on the dev-only API, arranges what the films
 * show (a clock face, an alert rule with smart snooze, quiet hours, a guest link, a public link),
 * then signs a browser in and visits every page:
 *
 *   NOCTURNE_API_URL=http://localhost:<api-port> node scripts/capture-features.js [feature...]
 *
 * Writes src/fixtures/features/<feature>.json in the shape `capture-fixtures.js` writes for
 * reports, `phases` included: when the feature's film has `do` actions they are performed in the
 * live app, for real, and what it answered after each is recorded. Also `params`, the route
 * parameters the capture resolved, which the composition puts on `page.params`, and `entries`, the
 * latest readings. Re-capturing changes the figures and names a film's notes quote.
 */
import { mkdirSync, writeFileSync } from "node:fs";
import { chromium } from "@playwright/test";
import { features } from "../src/features.js";
import { performFilmActions } from "./film-actions.js";
import { kindOf } from "./kind.js";
import { answersOf, recordRemote, scrollThrough } from "./record-remote.js";

const kind = kindOf(["--features"]);
const API = process.env.NOCTURNE_API_URL ?? "http://localhost:1610";
const names = process.argv.slice(2);
if (names.some((name) => !features[name])) {
  console.error(`usage: node scripts/capture-features.js [${Object.keys(features).join("|")}...]`);
  process.exit(1);
}

const seeded = await (
  await fetch(`${API}/api/v4/dev-only/admin/seed-tenant`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      slug: `films-${Date.now()}`,
      displayName: "Sample",
      ownerUsername: "dev",
      sampleData: true,
      sampleDataDays: 30,
    }),
  })
).json();
const host = new URL(seeded.url).host;

/** Calls the API as the tenant's owner; tenant resolution reads the forwarded host. */
async function api(path, { method = "GET", body } = {}) {
  const response = await fetch(`${API}${path}`, {
    method,
    headers: {
      Authorization: `Bearer ${seeded.accessToken}`,
      "X-Forwarded-Host": host,
      ...(body ? { "Content-Type": "application/json" } : {}),
    },
    ...(body ? { body: JSON.stringify(body) } : {}),
  });
  if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${await response.text()}`);
  return response.status === 204 ? null : response.json().catch(() => null);
}

/** What each feature's film needs in the tenant before its pages are visited; returns route parameters. */
const arrange = {
  async clocks() {
    const faces = await api("/api/v4/clockfaces");
    if (!faces?.[0]?.id) throw new Error("the sample tenant has no clock face");
    return { id: faces[0].id };
  },
  // Each sharing film starts from what it does not create itself.
  async caregivers() {
    const roles = await api("/api/v4/roles");
    const caretaker = roles.find((r) => r.slug === "caretaker")?.id;
    await api("/api/v4/member-invites", { method: "POST", body: { label: "Mum", roleIds: [caretaker] } });
    return {};
  },
  async "guest-links"() {
    await api("/api/v4/guest-links", { method: "POST", body: { label: "School nurse" } });
    return {};
  },
  // The film pairs the newest unattributed meal, so it is this one: its carbs fit the film's numbers
  // whatever the sample day holds.
  async food() {
    await api("/api/v4/nutrition/carbs", { method: "POST", body: { timestamp: new Date(Date.now() - 20 * 60_000).toISOString(), carbs: 21 } });
    return {};
  },
  // Created disabled, as the docs screenshots do: a rule the engine evaluated would raise live alerts.
  // Smart snooze starts off, for the film to turn on.
  async "smart-snooze"() {
    const rule = await api("/api/v4/alert-rules", {
      method: "POST",
      body: {
        name: "Low",
        severity: "warning",
        isEnabled: false,
        conditionType: "threshold",
        conditionParams: { direction: "below", value: 70 },
        channels: [{ channelType: "in_app" }],
        autoResolveEnabled: false,
        clientConfiguration: { snooze: { defaultMinutes: 15, options: [5, 15, 30, 60], maxCount: 3, smartSnooze: false } },
      },
    });
    return { id: rule.id };
  },
};

/** The readings the realtime store would have loaded, newest first, in the shape it keeps them. */
async function latestEntries() {
  const entries = await api("/api/v1/entries.json?count=36");
  return entries.map(({ sgv, date, direction, delta }) => ({
    type: "sgv",
    mills: date,
    sgv,
    direction,
    ...(delta == null ? {} : { delta }),
  }));
}

/**
 * The film with each action's `live` selector pressed in its place, where there is one: Playwright
 * will not press an element with no area (a replay's alert tick, an SVG line), which a film can. The
 * count of actions, and so the phase each records, is the film's.
 */
function live(film) {
  if (!film) return film;
  const swap = (spec) => (spec.live ? { click: spec.live } : spec);
  return {
    ...film,
    scenes: film.scenes.map((scene) => ({ ...scene, beats: scene.beats.map((beat) => ({ ...beat, do: beat.do?.map(swap) })) })),
  };
}

const out = new URL("../src/fixtures/features/", import.meta.url);
mkdirSync(out, { recursive: true });

const browser = await chromium.launch();
const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1440, height: 1000 } });
// The film has no coach marks (no provider), and in the live app they cover what the actions press.
await context.addInitScript(() => localStorage.setItem("nocturne:coach-marks-disabled", "true"));
const page = await context.newPage();
await page.goto(seeded.loginLink, { waitUntil: "networkidle", timeout: 180_000 });

for (const [name, { pages }] of Object.entries(features)) {
  if (!pages || (names.length && !names.includes(name))) continue;
  const film = kind.hasFilm(name) ? await kind.loadFilm(name) : null;
  const acts = film?.scenes.some((scene) => scene.beats.some((beat) => beat.do));
  if (acts && pages.length > 1) throw new Error(`${name}: a film with actions films one page`);
  const params = (await arrange[name]?.()) ?? {};
  const capturedAt = Date.now();

  const recording = await recordRemote(page, { label: name });
  for (const { route } of pages) {
    const path = route.replace(/\{(\w+)\}/g, (_, key) => params[key]);
    await page.goto(`${seeded.url}${path}`, { waitUntil: "networkidle", timeout: 180_000 });
    await scrollThrough(page, 20, 180_000);
  }
  // Back to the top, where the film starts, before acting.
  await page.mouse.wheel(0, -100_000);
  const acted = await performFilmActions(page, live(film), recording.setPhase);
  await page.waitForTimeout(1000);
  await recording.stop();
  const { phases } = recording;

  const route = pages[0].route.replace(/\{(\w+)\}/g, (_, key) => params[key]);
  const entries = await latestEntries();
  const fixture = {
    route,
    params,
    capturedAt,
    entries,
    ...answersOf(phases[0]),
    ...(acted ? { phases: phases.slice(1).map(answersOf) } : {}),
  };
  writeFileSync(new URL(`${name}.json`, out), JSON.stringify(fixture, null, 1) + "\n");
  const after = acted ? `; after ${acted} action(s): ${fixture.phases.map((p, i) => `${i + 1}:[${Object.keys(p.results).join(",")}]`).join(" ")}` : "";
  console.log(`${name}: ${Object.keys(fixture.results).sort().join(", ")}${after}`);
}
await browser.close();
await fetch(`${API}/api/v4/dev-only/admin/tenants/${seeded.tenantId}`, { method: "DELETE" }).catch(() => {});
