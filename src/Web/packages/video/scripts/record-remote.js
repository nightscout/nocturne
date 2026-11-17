/**
 * What a capture records from a live page: every remote function's answer, by the argument it was
 * asked with, and by phase (how many of a film's actions had run when the request went out).
 */
import { parse, stringify } from "devalue";
import { argKey } from "../src/replay/arg-key.js";

const isRemote = (url) => url.pathname.startsWith("/_app/remote/");

/**
 * Starts recording on `page`. `transform` rewrites each devalue-encoded result before it is kept.
 * Call `setPhase(k)` before the film's kth action and `stop()` once the page has settled; `phases`
 * holds what was recorded, one `{ asked, answers }` per phase.
 *
 * Bodies are read through a route, as the inspector drops a large one (90 days of readings) before it
 * can be read back. Responses can arrive out of order, so the order comes from the requests.
 */
export async function recordRemote(page, { label, transform = (encoded) => encoded }) {
  const phases = [{ asked: {}, answers: {} }];
  let current = 0;
  const record = async (intercepted) => {
    const url = new URL(intercepted.request().url());
    const fn = url.pathname.split("/").pop();
    const { asked, answers } = phases[current];
    const payload = url.searchParams.get("payload") ?? "";
    asked[fn] ??= [];
    if (!asked[fn].includes(payload)) asked[fn].push(payload);
    // Node cannot resolve a tenant's *.localhost name, so the fetch goes to the loopback address
    // and names the tenant in the Host header.
    const direct = new URL(url);
    if (direct.hostname.endsWith(".localhost")) direct.hostname = "127.0.0.1";
    const headers = { ...intercepted.request().headers(), host: url.host };
    const response = await intercepted.fetch({ url: direct.href, headers });
    const text = await response.text();
    await intercepted.fulfill({ response, body: text });
    const body = response.ok() ? JSON.parse(text) : null;
    if (body?.type !== "result") {
      console.warn(`${label}: ${fn} answered ${response.status()} ${body?.type ?? ""}; not recorded`);
      return;
    }
    answers[fn] ??= {};
    answers[fn][payload] = transform(body.result);
    // A command refreshes the queries it changes in its own response (SvelteKit's single-flight
    // mutation), so the page never asks for them again: record them as answers of this phase.
    for (const [key, entry] of Object.entries(body.refreshes ? parse(body.refreshes) : {})) {
      if (entry?.type !== "result") continue;
      const [, query, refreshed = ""] = key.split("/");
      asked[query] ??= [];
      if (!asked[query].includes(refreshed)) asked[query].push(refreshed);
      answers[query] ??= {};
      answers[query][refreshed] = transform(stringify(entry.data));
    }
  };
  await page.route(isRemote, record);
  return {
    phases,
    setPhase(k) {
      phases[k] = { asked: {}, answers: {} };
      current = k;
    },
    stop: () => page.unroute(isRemote, record),
  };
}

/** Scrolls the app's own scroll container, as some cards load their data only once in view. */
export async function scrollThrough(page, steps, timeout) {
  await page.mouse.move(900, 500);
  for (let step = 0; step < steps; step++) {
    await page.mouse.wheel(0, 700);
    await page.waitForTimeout(150);
  }
  await page.waitForLoadState("networkidle", { timeout });
  await page.waitForTimeout(1000);
}

/**
 * One phase's answers: `results` holds one per function; a function the page called with several
 * arguments (the two periods of a comparison) also keeps every answer, in call order, under `calls`,
 * and the argument each answered, under `args`.
 */
export function answersOf({ asked, answers }) {
  const results = {};
  const calls = {};
  const args = {};
  for (const [fn, byPayload] of Object.entries(answers)) {
    const payloads = (asked[fn] ?? []).filter((payload) => payload in byPayload);
    results[fn] = byPayload[payloads.at(-1)] ?? Object.values(byPayload).at(-1);
    if (payloads.length > 1) {
      calls[fn] = payloads.map((payload) => byPayload[payload]);
      args[fn] = payloads.map(payloadKey);
    }
  }
  return { results, ...(Object.keys(calls).length ? { calls, args } : {}) };
}

/** SvelteKit's own types in a remote function's argument: a sorted object, map or set. */
const revivers = {
  __skrao: (value) => value,
  __skram: (entries) => new Map(entries.map(([key, value]) => [parse(key, revivers), parse(value, revivers)])),
  __skras: (items) => new Set(items.map((item) => parse(item, revivers))),
};

/** The key replay gives the argument a request URL's `payload` carries (base64url devalue). */
function payloadKey(payload) {
  if (!payload) return "";
  return argKey(parse(Buffer.from(payload, "base64url").toString("utf8"), revivers));
}
