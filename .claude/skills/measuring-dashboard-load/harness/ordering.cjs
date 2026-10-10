// Usage: node ordering.cjs [cpuRate]
// Accurate browser-side timing (CDP monotonic timestamps) of when data requests are issued and
// answered, relative to the long tasks and LCP, on a signed-in empty-cache dashboard load.
const path = require("path");
const { createRequire } = require("module");
const { chromium } = createRequire(path.resolve(__dirname, "../../../..", "src/Web/packages/app/package.json"))("@playwright/test");
const O = "https://perf.nocturne.localhost:1631";
const rate = Number(process.argv[2] || 1);
(async () => {
  const b = await chromium.launch({ args: ["--host-resolver-rules=MAP *.nocturne.localhost 127.0.0.1"] });
  const l = await b.newContext({ ignoreHTTPSErrors: true }); const lp = await l.newPage();
  await lp.goto(O + "/"); await lp.waitForTimeout(2500); const st = await l.storageState(); await l.close();
  const c = await b.newContext({ ignoreHTTPSErrors: true, storageState: st, viewport: { width: 1440, height: 900 } });
  await c.addInitScript(() => {
    window.__lcp = []; window.__lt = [];
    new PerformanceObserver((l) => { for (const e of l.getEntries()) window.__lcp.push([Math.round(e.startTime), (e.element?.textContent || "").trim().slice(0, 30)]); }).observe({ type: "largest-contentful-paint", buffered: true });
    new PerformanceObserver((l) => { for (const e of l.getEntries()) window.__lt.push([Math.round(e.startTime), Math.round(e.duration)]); }).observe({ type: "longtask", buffered: true });
  });
  const p = await c.newPage(); const cdp = await c.newCDPSession(p);
  await cdp.send("Network.enable");
  await cdp.send("Network.emulateNetworkConditions", { offline: false, latency: 60, downloadThroughput: -1, uploadThroughput: -1 });
  await cdp.send("Emulation.setCPUThrottlingRate", { rate });
  let docTs = null; const reqs = new Map();
  cdp.on("Network.requestWillBeSent", (e) => {
    if (e.type === "Document" && docTs === null) docTs = e.timestamp;
    const u = new URL(e.request.url);
    if (/^\/(api|_app\/remote)\//.test(u.pathname)) reqs.set(e.requestId, { s: e.timestamp, path: u.pathname.replace(/^\/_app\/remote\/[^/]+\//, "remote:").replace(/^\/api\/v4\//, "api:") });
  });
  cdp.on("Network.loadingFinished", (e) => { const r = reqs.get(e.requestId); if (r) r.e = e.timestamp; });
  await p.goto(O + "/", { waitUntil: "load" }); await p.waitForTimeout(4000);
  const m = await p.evaluate(() => ({ lcp: window.__lcp, lt: window.__lt, navStart: performance.timeOrigin }));
  // performance.now() 0 is navigation start; the document request starts a few ms after it.
  const rel = (ts) => Math.round((ts - docTs) * 1000);
  console.log(`CPU x${rate}  long tasks >=50ms [start,dur]:`, JSON.stringify(m.lt.filter(([, d]) => d >= 50)));
  console.log("LCP:", m.lcp.map(([t, x]) => `${t} "${x}"`).join(" | "));
  const list = [...reqs.values()].sort((a, b) => a.s - b.s);
  console.log("first data request issued:", rel(list[0].s), "ms; realtime store wave (api:*) issued:", rel(list.find((r) => r.path.startsWith("api:")).s), "ms");
  for (const r of list.slice(0, 40)) console.log(String(rel(r.s)).padStart(6), String(r.e ? rel(r.e) : "-").padStart(6), r.path.slice(0, 60));
  await b.close();
})();
