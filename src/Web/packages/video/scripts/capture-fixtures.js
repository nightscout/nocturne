/**
 * Records what each report page's remote functions return on a running stack, so a composition
 * can replay the real page without one. Run against a seeded dev tenant:
 *
 *   node scripts/capture-fixtures.js https://<slug>.nocturne.localhost:<gateway-port> [report...]
 *
 * Dev auto-login signs the browser in. Writes src/fixtures/<report>.json for each named report (all
 * of them when none are named), holding each remote function's devalue-encoded result keyed by name
 * and the instant it was captured, which the composition pins its clock to so date ranges read as
 * they did then. Re-capturing a report changes the figures its film's notes quote.
 *
 * When the report's film has `do` actions, the capture performs them in the live app, in order,
 * and also records what the app answered after each one under `phases`: replay moves through them
 * as the film runs its actions. Those actions really happen, so a film that saves something saves
 * it in the tenant.
 */
import { writeFileSync, mkdirSync } from "node:fs";
import { chromium } from "@playwright/test";
import { parse, stringify } from "devalue";
import { reports } from "../src/reports.js";
import { performFilmActions } from "./film-actions.js";
import { kindOf } from "./kind.js";
import { answersOf, recordRemote, scrollThrough } from "./record-remote.js";

const kind = kindOf([]);
const [base, ...names] = process.argv.slice(2);
if (!base || names.some((name) => !reports[name])) {
  console.error(`usage: node scripts/capture-fixtures.js <tenant base url> [${Object.keys(reports).join("|")}...]`);
  process.exit(1);
}

/**
 * Report pages read the raw glucose entries only to count them, and those entries are most of a
 * fixture's size, so each keeps just its time and value.
 */
function slim(encoded) {
  const value = parse(encoded);
  if (!Array.isArray(value?.entries)) return encoded;
  return stringify({ ...value, entries: value.entries.map(({ mills, sgv }) => ({ mills, sgv })) });
}

const out = new URL("../src/fixtures/", import.meta.url);
mkdirSync(out, { recursive: true });

const browser = await chromium.launch();
const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1440, height: 1000 } });
const page = await context.newPage();

await page.goto(`${base}/`, { waitUntil: "networkidle", timeout: 120_000 });

for (const [name, { route }] of Object.entries(reports)) {
  if (names.length && !names.includes(name)) continue;
  const film = kind.hasFilm(name) ? await kind.loadFilm(name) : null;
  const capturedAt = Date.now();

  const recording = await recordRemote(page, { label: name, transform: slim });
  await page.goto(`${base}${route}`, { waitUntil: "networkidle", timeout: 120_000 });
  await scrollThrough(page, 40, 120_000);
  const acted = await performFilmActions(page, film, recording.setPhase);
  await page.waitForTimeout(1000);
  await recording.stop();
  const { phases } = recording;

  const fixture = { route, capturedAt, ...answersOf(phases[0]), ...(acted ? { phases: phases.slice(1).map(answersOf) } : {}) };
  writeFileSync(new URL(`${name}.json`, out), JSON.stringify(fixture, null, 1) + "\n");
  const several = Object.entries(fixture.calls ?? {}).map(([fn, list]) => `${fn} x${list.length}`);
  const after = acted ? `; after ${acted} action(s): ${fixture.phases.map((p, i) => `${i + 1}:[${Object.keys(p.results).join(",")}]`).join(" ")}` : "";
  console.log(`${name}: ${Object.keys(fixture.results).sort().join(", ")}${several.length ? ` (by call: ${several.join(", ")})` : ""}${after}`);
}
await browser.close();
