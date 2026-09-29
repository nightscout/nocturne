#!/usr/bin/env node
// Performance harness for the watercolour engine. See docs/watercolour/verification.md.
//
//   pnpm --filter @nocturne/watercolour bench -- [options]
//
//   --scenarios a,b&n=40   scenario list (default: all). `name&n=40` overrides the count.
//   --profiles p1,p2       desktop, laptop, phone-high, phone-low (default: all)
//   --repeats N            runs per scenario x profile (default 3)
//   --no-webgpu            also (or with --only-no-webgpu, only) run with navigator.gpu removed
//   --out DIR              default bench/results/<timestamp>
//   --headed               visible Chrome instead of --headless=new
//   --build                rebuild bench/dist even if it exists
//   --idle MS              idle window after settle (default 3000)
//   --url URL              measure an arbitrary page instead of a scenario (no readiness signal)
//   --wait SEC             --url only: seconds to observe after load (default 10)
//   --ignore-https-errors  --url only, for self-signed local hosts
//   --viewport-height PX   scenario viewport height for every profile (default 6000; see below)
//   --warm                 warm the engine before mounting (splits engine boot from per-artwork cost)
//
// The scheduler pauses an artwork that is outside the viewport, so a component below the fold never
// finishes and the scenario never signals ready. Scenarios are therefore measured in a viewport tall enough
// to hold the whole page; the profile only sets width, DPR and CPU rate. --url runs keep the profile height.
//
// CPU throttling is Emulation.setCPUThrottlingRate, which slows the renderer's main thread. The GPU
// (and the GPU process, and Dawn's command validation) cannot be throttled, so phone profiles model
// a slow CPU on a fast GPU: read them as a lower bound on what a real phone pays.

import { execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { brotliCompressSync, gzipSync } from 'node:zlib';
import os from 'node:os';

const here = dirname(fileURLToPath(import.meta.url));
const pkgDir = resolve(here, '..');
const require = createRequire(join(pkgDir, '../app/package.json'));
const { chromium } = require('@playwright/test');

const PROFILES = {
  desktop: { cpu: 1, viewport: { width: 1440, height: 900 }, dpr: 2 },
  laptop: { cpu: 2, viewport: { width: 1440, height: 900 }, dpr: 2 },
  'phone-high': { cpu: 4, viewport: { width: 390, height: 844 }, dpr: 3 },
  'phone-low': { cpu: 6, viewport: { width: 360, height: 740 }, dpr: 2 },
};

const clickAll = (sel, gap = 150) => async (page) => {
  const items = page.locator(sel);
  const count = await items.count();
  for (let i = 1; i < count; i++) {
    await items.nth(i).click();
    await page.waitForTimeout(gap);
  }
};
const hoverAll = (sel, dwell = 600) => async (page) => {
  const items = page.locator(sel);
  const count = await items.count();
  for (let i = 0; i < count; i++) {
    await items.nth(i).hover();
    await page.waitForTimeout(dwell);
  }
  await page.mouse.move(1, 1);
};

const SCENARIOS = {
  avatars: { query: 'n=40' },
  tabs: { query: 'n=5', interact: clickAll('[data-bench-tab]') },
  empty: { query: 'n=3' },
  hero: { query: '' },
  drops: { query: 'n=8', interact: hoverAll('.drop') },
  motif: { query: '' },
  edge: { query: 'n=8', interact: clickAll('[data-bench-edge]') },
  mixed: {
    query: '',
    interact: async (page) => {
      await clickAll('[data-bench-edge]')(page);
      await clickAll('[data-bench-tab]')(page);
    },
  },
};

function parseArgs(argv) {
  const opts = { repeats: 3, idle: 3000, wait: 10, scenarios: Object.keys(SCENARIOS), profiles: Object.keys(PROFILES), webgpu: [true], viewportHeight: 6000 };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const val = () => argv[++i];
    if (a === '--') continue;
    else if (a === '--scenarios') opts.scenarios = val().split(',').filter(Boolean);
    else if (a === '--profiles') opts.profiles = val().split(',').filter(Boolean);
    else if (a === '--repeats') opts.repeats = Number(val());
    else if (a === '--no-webgpu') opts.webgpu = [true, false];
    else if (a === '--only-no-webgpu') opts.webgpu = [false];
    else if (a === '--out') opts.out = resolve(val());
    else if (a === '--headed') opts.headed = true;
    else if (a === '--build') opts.build = true;
    else if (a === '--idle') opts.idle = Number(val());
    else if (a === '--url') opts.url = val();
    else if (a === '--wait') opts.wait = Number(val());
    else if (a === '--ignore-https-errors') opts.ignoreHttpsErrors = true;
    else if (a === '--warm') opts.warm = true;
    else if (a === '--viewport-height') opts.viewportHeight = Number(val());
    else throw new Error(`unknown option ${a}`);
  }
  for (const p of opts.profiles) if (!PROFILES[p]) throw new Error(`unknown profile ${p}`);
  opts.out ??= join(here, 'results', new Date().toISOString().replace(/[:.]/g, '-'));
  return opts;
}

const log = (m) => console.log(`[bench] ${m}`);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const finite = (xs) => xs.filter((x) => Number.isFinite(x));
const median = (xs) => {
  const s = finite(xs).sort((a, b) => a - b);
  if (!s.length) return NaN;
  const m = s.length >> 1;
  return s.length % 2 ? s[m] : (s[m - 1] + s[m]) / 2;
};
const pct = (xs, p) => {
  const s = finite(xs).sort((a, b) => a - b);
  return s.length ? s[Math.min(s.length - 1, Math.floor(s.length * p))] : NaN;
};
const mean = (xs) => (xs.length ? xs.reduce((a, b) => a + b, 0) / xs.length : NaN);
const max = (xs) => (xs.length ? Math.max(...xs) : 0);
const within = (xs, key, a, b) => xs.filter((x) => x[key] >= a && x[key] <= b);

function taskStats(tasks) {
  const d = tasks.map((t) => t.d);
  return { count: d.length, tbt: d.reduce((s, x) => s + Math.max(0, x - 50), 0), max: max(d) };
}
function loafStats(entries) {
  return { count: entries.length, blocking: entries.reduce((s, e) => s + (e.b ?? 0), 0), max: max(entries.map((e) => e.d)) };
}
const delta = (a, b) => Object.fromEntries(Object.keys(b).map((k) => [k, b[k] - (a[k] ?? 0)]));

// --- build + preview ---------------------------------------------------------------------------
async function startServer(opts) {
  const { build, preview } = await import('vite');
  const configFile = join(here, 'vite.config.ts');
  if (opts.build || !existsSync(join(here, 'dist/index.html'))) {
    log('vite build');
    await build({ configFile, logLevel: 'warn' });
  }
  const server = await preview({ configFile, logLevel: 'error', preview: { port: 4179, strictPort: false, host: '127.0.0.1' } });
  const url = server.resolvedUrls?.local?.[0] ?? 'http://127.0.0.1:4179/';
  return { url, close: () => new Promise((r) => server.httpServer.close(() => r())) };
}

// --- one run -----------------------------------------------------------------------------------
const CHROME_ARGS = [
  '--enable-unsafe-webgpu',
  '--enable-dawn-features=allow_unsafe_apis',
  '--enable-precise-memory-info',
  '--disable-background-timer-throttling',
  '--disable-renderer-backgrounding',
  '--disable-backgrounding-occluded-windows',
  '--no-first-run',
];

async function waitQuiet(page, quietMs = 700, maxMs = 60000) {
  const t0 = Date.now();
  for (;;) {
    const s = await page.evaluate(() => ({
      quiet: performance.now() - window.__benchProbe.lastActivity,
      pending: window.__bench?.pending?.() ?? 0,
    }));
    if (s.quiet >= quietMs && s.pending === 0) return true;
    if (Date.now() - t0 > maxMs) return false;
    await sleep(100);
  }
}

const pageNow = (page) => page.evaluate(() => performance.now());

const DUMP = () => {
  const P = window.__benchProbe;
  const canvases = P.canvasRefs.map((r) => r.deref()).filter((c) => c && c.isConnected);
  const mem = performance.memory;
  return {
    now: performance.now(),
    gpu: { ...P.gpu },
    raf: { ...P.raf },
    canvasesLive: canvases.length,
    canvasesTotal: P.canvasRefs.length,
    jsHeapUsed: mem?.usedJSHeapSize,
    firstPresentAt: P.firstPresentAt,
    longtasks: P.longtasks,
    loaf: P.loaf,
    events: P.events,
    interactions: P.interactions,
    frames: P.frames,
    wasm: P.wasm,
    gpuDone: P.gpuDone,
    bench: window.__bench && { ...window.__bench, pending: undefined },
    marks: performance.getEntriesByType('mark').map((m) => ({ name: m.name, t: m.startTime })),
    webgpu: typeof navigator.gpu !== 'undefined',
  };
};

function categorize(res) {
  const out = {};
  const add = (k, r) => {
    const o = (out[k] ??= { n: 0, encoded: 0, decoded: 0 });
    o.n++;
    o.encoded += r.encoded;
    o.decoded += r.decoded;
  };
  for (const r of res.values()) {
    if (!r.finished) continue;
    if (/\.wasm(\?|$)/.test(r.url)) add('wasm', r);
    else if (/nocturne_watercolour[^/]*\.js(\?|$)/.test(r.url)) add('glue', r);
    else if (r.type === 'Script' || /\.m?js(\?|$)/.test(r.url)) add('js', r);
    else if (r.mime.startsWith('image/')) add('image', r);
    else add('other', r);
  }
  return out;
}

async function runOnce(ctx, target, profileName, webgpu, repeat) {
  const { opts, baseUrl, instrument } = ctx;
  const profile = PROFILES[profileName];
  const browser = await chromium.launch({ channel: 'chrome', headless: !opts.headed, args: CHROME_ARGS });
  try {
    const context = await browser.newContext({
      viewport: { width: profile.viewport.width, height: opts.url ? profile.viewport.height : opts.viewportHeight },
      deviceScaleFactor: profile.dpr,
      ignoreHTTPSErrors: !!opts.ignoreHttpsErrors,
    });
    await context.addInitScript(
      webgpu ? instrument : `Object.defineProperty(Navigator.prototype, 'gpu', { get: () => undefined, configurable: true });\n${instrument}`,
    );
    const page = await context.newPage();
    const consoleErrors = [];
    page.on('pageerror', (e) => consoleErrors.push(String(e.message ?? e)));
    page.on('console', (m) => m.type() === 'error' && consoleErrors.length < 10 && consoleErrors.push(m.text()));
    const cdp = await context.newCDPSession(page);
    await cdp.send('Network.enable');
    await cdp.send('Network.setCacheDisabled', { cacheDisabled: true });
    await cdp.send('Emulation.setCPUThrottlingRate', { rate: profile.cpu });
    const res = new Map();
    cdp.on('Network.responseReceived', (e) =>
      res.set(e.requestId, { url: e.response.url, type: e.type, mime: e.response.mimeType ?? '', encoded: 0, decoded: 0, finished: false }));
    cdp.on('Network.dataReceived', (e) => { const r = res.get(e.requestId); if (r) r.decoded += e.dataLength; });
    cdp.on('Network.loadingFinished', (e) => { const r = res.get(e.requestId); if (r) { r.encoded = e.encodedDataLength; r.finished = true; } });

    const isUrl = !!target.url;
    const url = isUrl ? target.url : `${baseUrl}?s=${target.name}${target.query ? `&${target.query}` : ''}${opts.warm ? '&warm=1' : ''}`;
    await page.goto(url, { waitUntil: isUrl ? 'load' : 'commit', timeout: 60000 });

    let timedOut = false;
    if (isUrl) {
      await page.waitForTimeout(opts.wait * 1000);
      await waitQuiet(page, 1500, 30000);
    } else {
      await page.waitForFunction(() => window.__benchReady, null, { timeout: 300000, polling: 250 });
      const ready = await Promise.race([
        page.evaluate(() => window.__benchReady.then(() => true)),
        sleep(300000).then(() => false),
      ]);
      timedOut = !ready;
      await waitQuiet(page);
    }
    const settle = await page.evaluate(DUMP);
    const idleStart = await pageNow(page);
    await page.waitForTimeout(opts.idle);
    const idleEnd = await pageNow(page);
    const afterIdle = await page.evaluate(DUMP);

    let intStart = idleEnd;
    let intEnd = idleEnd;
    let afterInteract = afterIdle;
    if (!isUrl && target.interact) {
      intStart = await pageNow(page);
      await target.interact(page);
      await waitQuiet(page);
      intEnd = await pageNow(page);
      afterInteract = await page.evaluate(DUMP);
    }

    const engineStats = await page.evaluate(() => { try { return window.__benchEngineStats?.() ?? null; } catch { return null; } });
    await cdp.send('HeapProfiler.collectGarbage');
    await sleep(400);
    const heap = await cdp.send('Runtime.getHeapUsage');
    const final = await page.evaluate(DUMP);
    const adapter = await page.evaluate(async () => {
      if (!navigator.gpu) return null;
      try {
        const a = await navigator.gpu.requestAdapter();
        if (!a) return { adapter: null };
        const i = a.info ?? (a.requestAdapterInfo ? await a.requestAdapterInfo() : {});
        return { vendor: i.vendor, architecture: i.architecture, device: i.device, description: i.description, isFallback: a.isFallbackAdapter ?? i.isFallbackAdapter };
      } catch (e) { return { error: String(e) }; }
    });
    const version = browser.version();

    return summarize({ target, profileName, webgpu, repeat, settle, afterIdle, afterInteract, final, idleStart, idleEnd, intStart, intEnd, heap, engineStats, resources: categorize(res), adapter, version, timedOut, consoleErrors, isUrl });
  } finally {
    await browser.close();
  }
}

function summarize(r) {
  const { settle, afterIdle, afterInteract, final } = r;
  const b = final.bench ?? {};
  const mount = b.mountAt ?? 0;
  const marks = Object.fromEntries(final.marks.map((m) => [m.name, m.t]));
  const tracked = b.tracked ?? [];
  const modes = {};
  for (const t of tracked) modes[t.mode] = (modes[t.mode] ?? 0) + 1;
  const wasmInst = final.wasm.filter((w) => w.api.startsWith('instantiate') || w.api.startsWith('compile'));

  const loadEnd = r.idleStart;
  const loadFrames = within(final.frames, 'at', 0, loadEnd);
  const idleFrames = within(final.frames, 'at', r.idleStart, r.idleEnd);
  const intFrames = within(final.frames, 'at', r.intStart, r.intEnd);
  const perFrame = (fs) => ({
    frames: fs.length,
    passesMean: mean(fs.map((f) => f.passes)), passesMax: max(fs.map((f) => f.passes)),
    copiesMean: mean(fs.map((f) => f.copies)), copiesMax: max(fs.map((f) => f.copies)),
    submitsMean: mean(fs.map((f) => f.submits)), writesMean: mean(fs.map((f) => f.writes)),
  });

  const ev = within(final.events, 's', r.intStart, r.intEnd);
  const lat = within(final.interactions, 'at', r.intStart, r.intEnd).map((i) => i.d);
  const mb = (x) => (x == null ? NaN : x / 1048576);
  const gpuAt = (d) => d.gpu;
  const interactGpu = delta(afterIdle.gpu, afterInteract.gpu);
  const idleGpu = delta(settle.gpu, afterIdle.gpu);

  return {
    scenario: r.target.name ?? 'url', query: r.target.query ?? r.target.url, profile: r.profileName, webgpuRequested: r.webgpu, repeat: r.repeat,
    webgpuPresent: final.webgpu, presented: final.gpu.getCurrentTexture > 0, timedOut: r.timedOut || !!b.timedOut,
    timing: {
      readyMs: r.isUrl ? undefined : (b.readyAt ?? NaN) - mount,
      firstArtworkMs: r.isUrl ? (final.firstPresentAt ?? NaN) : (b.firstReadyAt ?? NaN) - mount,
      firstPresentMs: r.isUrl ? final.firstPresentAt : (final.firstPresentAt ?? NaN) - mount,
      mountAtMs: mount,
      engineInitMs: marks['watercolour:engine-ready'] - marks['watercolour:module-loaded'],
      moduleLoadedAtMs: marks['watercolour:module-loaded'],
      wasmInstantiateMs: wasmInst.length ? Math.max(...wasmInst.map((w) => w.ms)) : NaN,
      tracked: tracked.length, expected: b.expected ?? 0, modes,
    },
    load: { longtasks: taskStats(within(final.longtasks, 's', 0, loadEnd)), loaf: loafStats(within(final.loaf, 's', 0, loadEnd)) },
    idle: {
      rafPerSec: (afterIdle.raf.fired - settle.raf.fired) / ((r.idleEnd - r.idleStart) / 1000),
      submits: idleGpu.submits,
      longtasks: taskStats(within(final.longtasks, 's', r.idleStart, r.idleEnd)),
    },
    interact: {
      steps: lat.length,
      latencyMs: { median: median(lat), p95: pct(lat, 0.95), max: max(lat) },
      events: { count: ev.length, max: max(ev.map((e) => e.d)), p75: pct(ev.map((e) => e.d), 0.75) },
      longtasks: taskStats(within(final.longtasks, 's', r.intStart, r.intEnd)),
      loaf: loafStats(within(final.loaf, 's', r.intStart, r.intEnd)),
      gpu: interactGpu,
      perFrame: perFrame(intFrames),
    },
    gpu: {
      atSettle: gpuAt(settle),
      idleDelta: idleGpu,
      perFrameLoad: perFrame(loadFrames),
      perFrameIdle: perFrame(idleFrames),
      liveBytesFinal: final.gpu.liveBytes, liveBytesGcFinal: final.gpu.liveBytesGc,
      peakLiveBytes: final.gpu.peakLiveBytes, peakLiveBytesGc: final.gpu.peakLiveBytesGc,
      canvasesLiveAtSettle: settle.canvasesLive, canvasesTotal: final.canvasesTotal,
      submitDrainMsApprox: { n: final.gpuDone.length, median: median(final.gpuDone), p95: pct(final.gpuDone, 0.95), max: max(final.gpuDone) },
    },
    heap: { usedAtSettleMB: mb(settle.jsHeapUsed), usedAfterGcMB: mb(r.heap.usedSize), totalAfterGcMB: mb(r.heap.totalSize) },
    net: r.resources,
    engineStats: r.engineStats,
    adapter: r.adapter,
    chrome: r.version,
    consoleErrors: r.consoleErrors,
  };
}

// --- reporting ---------------------------------------------------------------------------------
function flatten(obj, prefix = '', out = {}) {
  for (const [k, v] of Object.entries(obj ?? {})) {
    const key = prefix ? `${prefix}.${k}` : k;
    if (typeof v === 'number') out[key] = v;
    else if (v && typeof v === 'object' && !Array.isArray(v)) flatten(v, key, out);
  }
  return out;
}

function medianRun(runs) {
  const flats = runs.map((r) => flatten(r));
  const keys = new Set(flats.flatMap((f) => Object.keys(f)));
  const m = {};
  for (const k of keys) m[k] = median(flats.map((f) => f[k]));
  return m;
}

const f0 = (x) => (Number.isFinite(x) ? Math.round(x).toString() : '-');
const f1 = (x) => (Number.isFinite(x) ? x.toFixed(1) : '-');
const kb = (x) => (Number.isFinite(x) ? Math.round(x / 1024).toString() : '-');
const mbs = (x) => (Number.isFinite(x) ? (x / 1048576).toFixed(1) : '-');

function markdown(groups, meta) {
  const L = [];
  L.push(`# Watercolour bench ${meta.startedAt}`, '');
  L.push(`Chrome ${meta.chrome}; adapter ${meta.adapter ? JSON.stringify(meta.adapter) : 'none'}; git ${meta.git}; ${meta.repeats} repeat(s), median shown.`);
  L.push('CPU throttling only slows the main thread. The GPU cannot be throttled, so phone rows understate a real phone.');
  L.push('Times are from scenario mount. `-` means not measured or not applicable. Preview serves uncompressed bytes.', '');
  L.push('## Timing and main thread', '');
  L.push('| scenario | profile | gpu | ready ms | 1st art ms | long tasks (n / TBT / max ms) | interact latency ms (med / max) | idle rAF/s | heap MB | net KB (wasm / js / img) |');
  L.push('|---|---|---|---|---|---|---|---|---|---|');
  for (const g of groups) {
    const m = g.median;
    L.push(`| ${g.scenario} | ${g.profile} | ${g.webgpu ? 'on' : 'off'}${g.timedOut ? ' TIMEOUT' : ''} | ${g.expected ? f0(m['timing.readyMs']) : '-'} | ${g.expected ? f0(m['timing.firstArtworkMs']) : '-'} | ${f0(m['load.longtasks.count'])} / ${f0(m['load.longtasks.tbt'])} / ${f0(m['load.longtasks.max'])} | ${f0(m['interact.latencyMs.median'])} / ${f0(m['interact.latencyMs.max'])} | ${f1(m['idle.rafPerSec'])} | ${f1(m['heap.usedAfterGcMB'])} | ${kb((m['net.wasm.encoded'] ?? 0) + (m['net.glue.encoded'] ?? 0))} wasm+glue / ${kb(m['net.js.encoded'])} / ${kb(m['net.image.encoded'])} |`);
  }
  L.push('', '## WebGPU work (load to settle)', '');
  L.push('| scenario | profile | gpu | encoders | passes (c+r) | copies | submits | writeBuffer KB | createBuffer MB | pipelines | peak live MB (gc-adj / destroy-only) | canvases | passes/frame mean / max | copies/frame max | submit drain ms (approx med / p95) |');
  L.push('|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|');
  for (const g of groups) {
    const m = g.median;
    const p = (k) => m[`gpu.atSettle.${k}`];
    L.push(`| ${g.scenario} | ${g.profile} | ${g.webgpu ? 'on' : 'off'} | ${f0(p('encoders'))} | ${f0(p('computePasses') + p('renderPasses'))} | ${f0(p('copyBufferToBuffer') + p('copyBufferToTexture') + p('copyTextureToTexture'))} | ${f0(p('submits'))} | ${kb(p('writeBufferBytes'))} | ${mbs(p('createBufferBytes'))} | ${f0(p('computePipelines') + p('renderPipelines'))} | ${mbs(m['gpu.peakLiveBytesGc'])} / ${mbs(m['gpu.peakLiveBytes'])} | ${f0(m['gpu.canvasesLiveAtSettle'])} | ${f1(m['gpu.perFrameLoad.passesMean'])} / ${f0(m['gpu.perFrameLoad.passesMax'])} | ${f0(m['gpu.perFrameLoad.copiesMax'])} | ${f1(m['gpu.submitDrainMsApprox.median'])} / ${f1(m['gpu.submitDrainMsApprox.p95'])} |`);
  }
  L.push('', '## Interaction phase (tabs, edge, drops, mixed)', '');
  L.push('| scenario | profile | gpu | steps | latency ms (med / p95 / max) | event timing max ms | long tasks (n / TBT / max ms) | passes | copies | submits | writeBuffer KB | createBuffer MB | pipelines |');
  L.push('|---|---|---|---|---|---|---|---|---|---|---|---|---|');
  for (const g of groups) {
    const m = g.median;
    if (!(m['interact.steps'] > 0)) continue;
    const p = (k) => m[`interact.gpu.${k}`];
    L.push(`| ${g.scenario} | ${g.profile} | ${g.webgpu ? 'on' : 'off'} | ${f0(m['interact.steps'])} | ${f0(m['interact.latencyMs.median'])} / ${f0(m['interact.latencyMs.p95'])} / ${f0(m['interact.latencyMs.max'])} | ${f0(m['interact.events.max'])} | ${f0(m['interact.longtasks.count'])} / ${f0(m['interact.longtasks.tbt'])} / ${f0(m['interact.longtasks.max'])} | ${f0(p('computePasses') + p('renderPasses'))} | ${f0(p('copyBufferToBuffer') + p('copyBufferToTexture') + p('copyTextureToTexture'))} | ${f0(p('submits'))} | ${kb(p('writeBufferBytes'))} | ${mbs(p('createBufferBytes'))} | ${f0(p('computePipelines') + p('renderPipelines'))} |`);
  }
  L.push('', 'Latency is input event to the second following frame; it does not include work the engine does after that (a paint that lands later shows up in the long-task and GPU columns).');
  L.push('Live GPU bytes are estimates from createBuffer/createTexture sizes minus destroy() and, for gc-adjusted, finalized wrappers; swapchain textures are not counted. Peak covers the whole run including interactions.', '');
  return L.join('\n');
}

// --- main --------------------------------------------------------------------------------------
async function main() {
  const opts = parseArgs(process.argv.slice(2));
  mkdirSync(opts.out, { recursive: true });
  const instrument = readFileSync(join(here, 'instrument.js'), 'utf8');
  const startedAt = new Date().toISOString();

  let server;
  let baseUrl;
  if (!opts.url) {
    server = await startServer(opts);
    baseUrl = server.url;
  }
  const ctx = { opts, baseUrl, instrument };

  const targets = opts.url
    ? [{ url: opts.url, name: 'url' }]
    : opts.scenarios.map((s) => {
        const [name, ...rest] = s.split('&');
        if (!SCENARIOS[name]) throw new Error(`unknown scenario ${name}`);
        const base = SCENARIOS[name];
        return { name, query: rest.length ? rest.join('&') : base.query, interact: base.interact };
      });

  const meta = { startedAt, repeats: opts.repeats, git: 'unknown', os: `${os.platform()} ${os.release()} ${os.cpus()[0]?.model}`, node: process.version, args: process.argv.slice(2) };
  try { meta.git = execFileSync('git', ['rev-parse', '--short', 'HEAD'], { cwd: pkgDir }).toString().trim(); } catch {}
  try { meta.gitDirty = execFileSync('git', ['status', '--porcelain', '--', pkgDir, resolve(pkgDir, '../../../../crates')], { cwd: pkgDir }).toString().trim().length > 0; } catch {}
  const wasmPath = join(pkgDir, 'src/wasm/nocturne_watercolour_bg.wasm');
  if (existsSync(wasmPath)) {
    const buf = readFileSync(wasmPath);
    meta.wasm = { bytes: buf.length, gzip: gzipSync(buf, { level: 9 }).length, brotli: brotliCompressSync(buf).length, mtime: statSync(wasmPath).mtime.toISOString() };
  }

  const groups = [];
  let adapter;
  for (const target of targets) {
    for (const profile of opts.profiles) {
      for (const webgpu of opts.webgpu) {
        const runs = [];
        for (let i = 0; i < opts.repeats; i++) {
          const label = `${target.name}${target.query ? `&${target.query}` : ''} ${profile} gpu=${webgpu ? 'on' : 'off'} #${i + 1}`;
          log(label);
          try {
            const run = await runOnce(ctx, target, profile, webgpu, i);
            runs.push(run);
            adapter ??= webgpu ? run.adapter : adapter;
            meta.chrome ??= run.chrome;
            if (webgpu && !run.presented && !target.url) log(`  warning: no WebGPU present (webgpuPresent=${run.webgpuPresent}, modes=${JSON.stringify(run.timing.modes)})`);
            if (run.timedOut) log('  warning: scenario timed out before readiness');
            writeFileSync(join(opts.out, `${target.name}_${profile}_gpu-${webgpu ? 'on' : 'off'}_r${i + 1}.json`), JSON.stringify(run, null, 2));
          } catch (e) {
            log(`  FAILED: ${e.message}`);
          }
        }
        if (runs.length) groups.push({ scenario: target.name, profile, webgpu, runs: runs.length, expected: runs[0].timing.expected, timedOut: runs.some((r) => r.timedOut), median: medianRun(runs) });
      }
    }
  }
  meta.adapter = adapter;
  meta.caveat = 'CPU throttling only; the GPU cannot be throttled.';
  writeFileSync(join(opts.out, 'summary.json'), JSON.stringify({ meta, groups }, null, 2));
  const md = markdown(groups, meta);
  writeFileSync(join(opts.out, 'summary.md'), md);
  console.log(`\n${md}\nresults in ${opts.out}`);
  await server?.close();
}

main().then(
  () => process.exit(0),
  (e) => {
    console.error(e);
    process.exit(1);
  },
);
