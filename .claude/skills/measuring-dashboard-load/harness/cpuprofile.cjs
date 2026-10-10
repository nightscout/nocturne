// Usage: node cpuprofile.cjs <cpuThrottleRate> [runs]  (writes profile-x<rate>.cpuprofile next to this script)
// CPU profile of a signed-in, empty-cache dashboard load until shortly after LCP. Reports main
// thread self-time by category and by script, plus long tasks.
const path = require("path");
const { createRequire } = require("module");
const fs = require("fs");
const { chromium } = createRequire(path.resolve(__dirname, "../../../..", "src/Web/packages/app/package.json"))("@playwright/test");
const O = "https://perf.nocturne.localhost:1631";
const rate = Number(process.argv[2] || 1), runs = Number(process.argv[3] || 3);
(async () => {
  const b = await chromium.launch({ args: ["--host-resolver-rules=MAP *.nocturne.localhost 127.0.0.1"] });
  const l = await b.newContext({ ignoreHTTPSErrors: true }); const lp = await l.newPage();
  await lp.goto(O + "/"); await lp.waitForTimeout(2500); const st = await l.storageState(); await l.close();
  const agg = {}, aggCat = {}; let total = 0; const lcps = []; const longTasks = [];
  for (let i = 0; i < runs; i++) {
    const c = await b.newContext({ ignoreHTTPSErrors: true, storageState: st, viewport: { width: 1440, height: 900 } });
    await c.addInitScript(() => {
      window.__lcp = 0; window.__lt = [];
      new PerformanceObserver((l) => { for (const e of l.getEntries()) window.__lcp = e.startTime; }).observe({ type: "largest-contentful-paint", buffered: true });
      new PerformanceObserver((l) => { for (const e of l.getEntries()) window.__lt.push([Math.round(e.startTime), Math.round(e.duration)]); }).observe({ type: "longtask", buffered: true });
    });
    const p = await c.newPage(); const cdp = await c.newCDPSession(p);
    await cdp.send("Network.enable");
    await cdp.send("Network.emulateNetworkConditions", { offline: false, latency: 60, downloadThroughput: -1, uploadThroughput: -1 });
    await cdp.send("Emulation.setCPUThrottlingRate", { rate });
    await cdp.send("Profiler.enable"); await cdp.send("Profiler.setSamplingInterval", { interval: 200 });
    await cdp.send("Profiler.start");
    await p.goto(O + "/", { waitUntil: "load" }); await p.waitForTimeout(3500);
    const { profile } = await cdp.send("Profiler.stop");
    const m = await p.evaluate(() => ({ lcp: Math.round(window.__lcp), lt: window.__lt }));
    lcps.push(m.lcp); longTasks.push(m.lt);
    if (i === 0) fs.writeFileSync(path.join(__dirname, `profile-x${rate}.cpuprofile`), JSON.stringify(profile));
    const byId = new Map(profile.nodes.map((n) => [n.id, n]));
    const dt = profile.timeDeltas; const counts = new Map();
    profile.samples.forEach((id, k) => counts.set(id, (counts.get(id) ?? 0) + (dt[k] ?? 0)));
    for (const [id, us] of counts) {
      const n = byId.get(id); const f = n.callFrame; const ms = us / 1000;
      let key;
      if (!f.url) key = f.functionName ? `(${f.functionName})`.replace("((", "(").replace("))", ")") : "(native)";
      else key = f.url.replace(O, "").replace(/^\/_app\/immutable\//, "");
      if (key === "(idle)") continue;
      total += ms;
      agg[key] = (agg[key] ?? 0) + ms;
      const cat = !f.url ? key : f.url.includes("/_app/immutable/") ? "app js" : f.url.startsWith(O) ? "inline/page js" : "other";
      aggCat[cat] = (aggCat[cat] ?? 0) + ms;
    }
    await c.close();
  }
  await b.close();
  const per = (x) => (x / runs).toFixed(0).padStart(6);
  console.log(`CPU x${rate}: LCP ${lcps.join(", ")} ms; busy (non-idle) ${per(total)} ms/run`);
  console.log("long tasks run1 [start,dur]:", JSON.stringify(longTasks[0].filter(([, d]) => d >= 50)));
  console.log("by category (ms/run):"); for (const [k, v] of Object.entries(aggCat).sort((a, b) => b[1] - a[1]).slice(0, 8)) console.log(per(v), k);
  console.log("top scripts (ms/run):"); for (const [k, v] of Object.entries(agg).sort((a, b) => b[1] - a[1]).slice(0, 22)) console.log(per(v), k);
})();
