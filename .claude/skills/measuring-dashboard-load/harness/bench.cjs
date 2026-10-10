// Usage: node bench.cjs <label>
// Sequential request micro-benchmarks from inside a signed-in demo tenant page.
const path = require("path");
const { createRequire } = require("module");
const appRequire = createRequire(path.resolve(__dirname, "../../../..", "src/Web/packages/app/package.json"));
const { chromium } = appRequire("@playwright/test");


const ORIGIN = process.env.ORIGIN || "https://perf.nocturne.localhost:1631";

(async () => {
  // Without the mapping Chrome spends ~550 ms resolving *.nocturne.localhost on every fresh context.
  const browser = await chromium.launch({ args: ["--host-resolver-rules=MAP *.nocturne.localhost 127.0.0.1"] });
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await context.newPage();
  await page.goto(`${ORIGIN}/api/v4/dev-only/auth/login?redirect=%2F`, { waitUntil: "load", timeout: 120000 });
  await page.waitForTimeout(5000);

  const result = await page.evaluate(async () => {
    const med = (xs) => { const s = [...xs].sort((a, b) => a - b); return Math.round(s[Math.floor(s.length / 2)]); };
    async function time(n, fn) {
      await fn(); // warm
      const xs = [];
      for (let i = 0; i < n; i++) { const t = performance.now(); await fn(); xs.push(performance.now() - t); }
      return { median_ms: med(xs), min_ms: Math.round(Math.min(...xs)) };
    }
    const ok = async (r) => { if (!r.ok) throw new Error(`${r.url} ${r.status}`); await r.arrayBuffer(); };
    const to = new Date().toISOString();
    const from = new Date(Date.now() - 86400000).toISOString();
    return {
      proxied_api_profile_summary: await time(20, async () => ok(await fetch("/api/v4/profile/summary"))),
      proxied_api_boluses_24h: await time(20, async () => ok(await fetch(`/api/v4/insulin/boluses?from=${from}&to=${to}&limit=500`))),
      dashboard_ssr_document: await time(10, async () => ok(await fetch("/", { headers: { accept: "text/html" } }))),
      summary: await time(15, async () => ok(await fetch("/api/v4/summary"))),
      reports_ssr_document: await time(10, async () => ok(await fetch("/reports/agp", { headers: { accept: "text/html" } }))),
    };
  });
  console.log(JSON.stringify({ label: process.argv[2] || "run", ...result }, null, 2));
  await browser.close();
})().catch((e) => { console.error(e); process.exit(1); });
