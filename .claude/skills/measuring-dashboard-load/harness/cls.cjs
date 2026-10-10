// Usage: node cls.cjs [cpuRate]  - attribute layout shifts on a signed-in empty-cache dashboard load.
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
    window.__shifts = [];
    const describe = (n) => {
      if (!n) return "?";
      const el = n.nodeType === 3 ? n.parentElement : n;
      if (!el) return "?";
      const id = el.id ? "#" + el.id : "";
      const cls = (el.className && typeof el.className === "string") ? "." + el.className.trim().split(/\s+/).slice(0, 3).join(".") : "";
      const text = (el.textContent || "").trim().slice(0, 40).replace(/\s+/g, " ");
      return `${el.tagName.toLowerCase()}${id}${cls} "${text}"`;
    };
    new PerformanceObserver((list) => {
      for (const e of list.getEntries()) {
        if (e.hadRecentInput) continue;
        window.__shifts.push({
          t: Math.round(e.startTime), value: Math.round(e.value * 1000),
          sources: (e.sources || []).slice(0, 4).map((s) => ({
            node: describe(s.node),
            from: s.previousRect ? [s.previousRect.x, s.previousRect.y, s.previousRect.width, s.previousRect.height] : null,
            to: s.currentRect ? [s.currentRect.x, s.currentRect.y, s.currentRect.width, s.currentRect.height] : null,
          })),
        });
      }
    }).observe({ type: "layout-shift", buffered: true });
  });
  const p = await c.newPage(); const cdp = await c.newCDPSession(p);
  await cdp.send("Network.enable");
  await cdp.send("Network.emulateNetworkConditions", { offline: false, latency: 60, downloadThroughput: -1, uploadThroughput: -1 });
  await cdp.send("Emulation.setCPUThrottlingRate", { rate });
  await p.goto(O + "/", { waitUntil: "load" }); await p.waitForTimeout(6000);
  const shifts = await p.evaluate(() => window.__shifts);
  let total = 0;
  for (const s of shifts) {
    total += s.value;
    console.log(`t=${s.t}ms value=${s.value} milli`);
    for (const src of s.sources) console.log(`    ${src.node}\n      from ${JSON.stringify(src.from)} to ${JSON.stringify(src.to)}`);
  }
  console.log(`total CLS ${total} milli over ${shifts.length} shifts`);
  await b.close();
})();
