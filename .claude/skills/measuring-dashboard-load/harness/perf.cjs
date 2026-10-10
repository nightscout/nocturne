// Usage: node perf.cjs <label> [runs]
// Page-load scenarios against the e2e production stack behind the Caddy h2 edge, with
// LATENCY_MS (default 60, the edge RTT) added per request by CDP; the compose delay proxy adds
// ORIGIN_DELAY_MS (320) to everything but edge-cached assets, matching the ~380 ms origin RTT.
//   signedOutCold  first visit, no cookies, empty cache (auto-login redirect chain)
//   signedInCold   session cookie, empty cache
//   signedInWarm   session cookie, HTTP cache primed by a previous visit
const path = require("path");
const { createRequire } = require("module");
const appRequire = createRequire(path.resolve(__dirname, "../../../..", "src/Web/packages/app/package.json"));
const { chromium } = appRequire("@playwright/test");

const ORIGIN = process.env.ORIGIN || "https://perf.nocturne.localhost:1631";
const LATENCY_MS = Number(process.env.LATENCY_MS || 60); // edge RTT; the delay proxy adds the origin leg
const label = process.argv[2] || "run";
const runs = Number(process.argv[3] || 5);
const median = (xs) => { const s = xs.filter((x) => x >= 0).sort((a, b) => a - b); return s.length ? s[Math.floor(s.length / 2)] : -1; };
const ignore = (u) => /socket\.io|\/realtime|\/ws\b|\/health/.test(u);

async function newPage(browser, storageState) {
  const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1440, height: 900 }, storageState });
  await context.addInitScript(() => {
    window.__lcp = -1; window.__cls = 0;
    new PerformanceObserver((l) => { for (const e of l.getEntries()) if (!e.hadRecentInput) window.__cls += e.value; })
      .observe({ type: "layout-shift", buffered: true });
    // Every candidate, so the dashboard's own content can be told apart from the account-specific
    // "add a backup way to sign in" banner, which paints last for a single-passkey test account.
    window.__lcpAll = [];
    // When the main plot's glucose line first has a shape, taken at the next frame as an
    // approximation of its paint. SVG charts are not LCP candidates, so this is tracked apart.
    window.__plot = -1;
    const plotWatch = new MutationObserver(() => {
      const line = document.querySelector("svg path.stroke-2.fill-none[d]");
      if (line && line.getAttribute("d")?.length > 20) {
        plotWatch.disconnect();
        requestAnimationFrame(() => (window.__plot = performance.now()));
      }
    });
    document.addEventListener("DOMContentLoaded", () => plotWatch.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ["d"] }));
    new PerformanceObserver((l) => { for (const e of l.getEntries()) { window.__lcp = e.startTime; window.__lcpAll.push([e.startTime, (e.element?.textContent || "").slice(0, 80)]); } })
      .observe({ type: "largest-contentful-paint", buffered: true });
  });
  const page = await context.newPage();
  const cdp = await context.newCDPSession(page);
  await cdp.send("Network.enable");
  await cdp.send("Network.emulateNetworkConditions", { offline: false, latency: LATENCY_MS, downloadThroughput: -1, uploadThroughput: -1 });
  if (process.env.PERF_CPU) await cdp.send("Emulation.setCPUThrottlingRate", { rate: Number(process.env.PERF_CPU) });
  return { context, page };
}

async function measure(page, url) {
  let inflight = 0, last = Date.now(), docs = 0, dataRequests = 0;
  const onReq = (r) => {
    if (ignore(r.url())) return;
    inflight++; last = Date.now();
    if (r.resourceType() === "document") docs++;
    if (/\/_app\/remote\/|\/api\//.test(r.url()) && r.resourceType() !== "document") dataRequests++;
  };
  const done = (r) => { if (!ignore(r.url())) { inflight = Math.max(0, inflight - 1); last = Date.now(); } };
  page.on("request", onReq); page.on("requestfinished", done); page.on("requestfailed", done);
  const t0 = Date.now();
  // Wall-clock from goto: in-page timing omits part of the latency CDP emulation adds.
  await page.goto(url, { waitUntil: "load", timeout: 180000 });
  const loadMs = Date.now() - t0;
  // Network quiet alone ends too early under CPU throttling: a multi-second hydration task issues
  // nothing, so the page also has to have drawn its main plot (capped at 30 s).
  while (Date.now() - t0 < 120000) {
    await page.waitForTimeout(100);
    if (inflight !== 0 || Date.now() - last < 1500) continue;
    const plotted = await page.evaluate(() => window.__plot >= 0).catch(() => false);
    if (plotted || Date.now() - t0 > 30000) break;
  }
  page.off("request", onReq); page.off("requestfinished", done); page.off("requestfailed", done);
  const nav = await page.evaluate(() => {
    const n = performance.getEntriesByType("navigation")[0];
    const fcp = performance.getEntriesByName("first-contentful-paint")[0];
    const res = performance.getEntriesByType("resource");
    const blockingCss = res.filter((r) => r.initiatorType === "link" && r.name.endsWith(".css") && r.renderBlockingStatus === "blocking");
    return {
      timeOrigin: performance.timeOrigin,
      redirectMs: Math.round(n.redirectEnd - n.redirectStart),
      ttfbMs: Math.round(n.responseStart),
      domContentLoadedMs: Math.round(n.domContentLoadedEventEnd),
      fcpMs: Math.round(fcp?.startTime ?? -1),
      lcpMs: Math.round(window.__lcp),
      plotMs: Math.round(window.__plot),
      dashLcpMs: Math.round(window.__lcpAll.filter(([, t]) => !/one way into this account/.test(t)).pop()?.[0] ?? -1),
      clsMilli: Math.round(window.__cls * 1000),
      blockingCss: blockingCss.length,
      blockingCssBytes: blockingCss.reduce((a, r) => a + (r.encodedBodySize || 0), 0),
      jsChunks: res.filter((r) => r.name.endsWith(".js")).length,
      finalUrl: location.pathname + location.search,
    };
  });
  const wall = (ms) => (ms < 0 ? -1 : Math.round(nav.timeOrigin + ms - t0));
  const { timeOrigin, ...rest } = nav;
  return {
    fcpWallMs: wall(nav.fcpMs), lcpWallMs: wall(nav.lcpMs), dashLcpWallMs: wall(nav.dashLcpMs), plotWallMs: wall(nav.plotMs), ttfbWallMs: wall(nav.ttfbMs),
    loadMs, settledMs: last - t0, documents: docs, redirects: docs - 1, dataRequests, ...rest,
  };
}

function summarise(results) {
  const keys = Object.keys(results[0]).filter((k) => typeof results[0][k] === "number");
  return Object.fromEntries(keys.map((k) => [k, median(results.map((r) => r[k]))]));
}

(async () => {
  // Without the mapping Chrome spends ~550 ms resolving *.nocturne.localhost on every fresh context.
  const browser = await chromium.launch({ args: ["--host-resolver-rules=MAP *.nocturne.localhost 127.0.0.1"] });
  const out = { label, latencyMs: LATENCY_MS, runs, scenarios: {} };

  // Sign in once (no latency) to get a session for the signed-in scenarios.
  const login = await browser.newContext({ ignoreHTTPSErrors: true });
  const lp = await login.newPage();
  await lp.goto(`${ORIGIN}/`, { waitUntil: "load", timeout: 180000 });
  await lp.waitForTimeout(3000);
  const storageState = await login.storageState();
  if (!storageState.cookies.length) throw new Error(`no session after auto-login (landed on ${lp.url()})`);
  await login.close();

  const scenarios = {
    signedOutCold: async () => { const { context, page } = await newPage(browser); try { return await measure(page, `${ORIGIN}/`); } finally { await context.close(); } },
    signedInCold: async () => { const { context, page } = await newPage(browser, storageState); try { return await measure(page, `${ORIGIN}/`); } finally { await context.close(); } },
    signedInWarm: async () => {
      const { context, page } = await newPage(browser, storageState);
      try { await measure(page, `${ORIGIN}/`); return await measure(page, `${ORIGIN}/`); } finally { await context.close(); }
    },
  };

  for (const [name, fn] of Object.entries(scenarios)) {
    await fn(); // discarded warm-up: server JIT and caches
    const results = [];
    for (let i = 0; i < runs; i++) results.push(await fn());
    out.scenarios[name] = { median: summarise(results), finalUrl: results[0].finalUrl, raw: results };
    console.error(`${label} ${name}: FCP ${out.scenarios[name].median.fcpWallMs} ms, LCP ${out.scenarios[name].median.lcpWallMs} ms (wall), redirects ${out.scenarios[name].median.redirects}`);
  }
  await browser.close();
  console.log(JSON.stringify(out, null, 2));
})().catch((e) => { console.error(e); process.exit(1); });
