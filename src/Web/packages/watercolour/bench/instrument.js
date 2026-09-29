// Injected before any page script (Playwright addInitScript). Plain script, no modules: it only
// observes. Everything run.mjs reads hangs off window.__benchProbe.
(() => {
  if (window.__benchProbe) return;
  const now = () => performance.now();
  const P = (window.__benchProbe = {
    marks: [],
    longtasks: [],
    loaf: [],
    events: [],
    interactions: [],
    frames: [],
    wasm: [],
    gpuDone: [],
    firstPresentAt: undefined,
    lastActivity: now(),
    raf: { requested: 0, fired: 0 },
    gpu: {
      encoders: 0, computePasses: 0, renderPasses: 0, dispatches: 0,
      copyBufferToBuffer: 0, copyBufferToTexture: 0, copyTextureToTexture: 0, clearBuffer: 0,
      submits: 0, writeBuffer: 0, writeBufferBytes: 0, writeTexture: 0, writeTextureBytes: 0,
      createBuffer: 0, createBufferBytes: 0, createTexture: 0, createTextureBytes: 0,
      computePipelines: 0, renderPipelines: 0, bindGroups: 0, getCurrentTexture: 0,
      liveBytes: 0, peakLiveBytes: 0, liveBytesGc: 0, peakLiveBytesGc: 0,
    },
    canvasRefs: [],
    mark(name) { this.marks.push({ name, t: now() }); },
  });
  const G = P.gpu;
  const active = () => { P.lastActivity = now(); };

  const observe = (type, opts, fn) => {
    try {
      new PerformanceObserver((list) => list.getEntries().forEach(fn)).observe({ type, buffered: true, ...opts });
    } catch {}
  };
  observe('longtask', {}, (e) => P.longtasks.push({ s: e.startTime, d: e.duration }));
  observe('long-animation-frame', {}, (e) => P.loaf.push({ s: e.startTime, d: e.duration, b: e.blockingDuration }));
  observe('event', { durationThreshold: 16 }, (e) =>
    P.events.push({ n: e.name, s: e.startTime, d: e.duration, ip: e.processingStart - e.startTime }));

  const GAUGE = ['computePasses', 'renderPasses', 'copyBufferToBuffer', 'copyBufferToTexture', 'copyTextureToTexture', 'clearBuffer', 'submits', 'writeBuffer', 'writeTexture', 'encoders', 'dispatches'];
  const snap = () => { const o = {}; for (const k of GAUGE) o[k] = G[k]; return o; };

  const origRaf = window.requestAnimationFrame.bind(window);
  window.requestAnimationFrame = (cb) => {
    P.raf.requested++;
    return origRaf((ts) => {
      P.raf.fired++;
      active();
      const before = snap();
      try { cb(ts); } finally {
        const a = snap();
        const f = {
          at: ts,
          passes: (a.computePasses - before.computePasses) + (a.renderPasses - before.renderPasses),
          copies: (a.copyBufferToBuffer - before.copyBufferToBuffer) + (a.copyBufferToTexture - before.copyBufferToTexture) + (a.copyTextureToTexture - before.copyTextureToTexture) + (a.clearBuffer - before.clearBuffer),
          submits: a.submits - before.submits,
          encoders: a.encoders - before.encoders,
          dispatches: a.dispatches - before.dispatches,
          writes: (a.writeBuffer - before.writeBuffer) + (a.writeTexture - before.writeTexture),
        };
        if ((f.submits || f.passes || f.copies || f.writes) && P.frames.length < 5000) P.frames.push(f);
      }
    });
  };

  // Interaction latency: input event to the second frame after it (the first frame that can show its effect).
  const near = (el) => el && el.closest && el.closest('[data-bench-tab],[data-bench-edge],.drop');
  const latency = (type) => (e) => {
    const target = near(e.target);
    if (!target) return;
    if (type === 'pointerover' && near(e.relatedTarget) === target) return;
    const t = e.timeStamp;
    origRaf(() => origRaf(() => P.interactions.push({ type, at: t, d: now() - t })));
  };
  document.addEventListener('click', latency('click'), true);
  document.addEventListener('pointerover', latency('pointerover'), true);

  for (const api of ['instantiateStreaming', 'compileStreaming', 'instantiate', 'compile']) {
    const orig = WebAssembly[api];
    if (typeof orig !== 'function') continue;
    WebAssembly[api] = function (...a) {
      const t = now();
      const r = orig.apply(this, a);
      const done = () => P.wasm.push({ api, start: t, ms: now() - t });
      Promise.resolve(r).then(done, done);
      return r;
    };
  }

  const gc = HTMLCanvasElement.prototype.getContext;
  HTMLCanvasElement.prototype.getContext = function (type, ...a) {
    const ctx = gc.call(this, type, ...a);
    if (type === 'webgpu' && ctx) P.canvasRefs.push(new WeakRef(this));
    return ctx;
  };

  if (typeof GPUDevice === 'undefined' || !navigator.gpu) return;

  const BPP = { r: 1, rg: 2, rgba: 4, bgra: 4 };
  const texBytes = (d) => {
    const s = d.size;
    const w = Array.isArray(s) ? s[0] : s.width;
    const h = (Array.isArray(s) ? s[1] : s.height) ?? 1;
    const l = (Array.isArray(s) ? s[2] : s.depthOrArrayLayers) ?? 1;
    const m = /^(r|rg|rgba|bgra)(\d+)/.exec(d.format || '');
    const bpp = m ? BPP[m[1]] * (Number(m[2]) / 8) : 4;
    const mip = (d.mipLevelCount || 1) > 1 ? 4 / 3 : 1;
    return Math.round(w * h * l * bpp * mip * (d.sampleCount || 1));
  };

  const wrap = (proto, name, hook) => {
    const orig = proto && proto[name];
    if (typeof orig !== 'function') return;
    proto[name] = function (...a) {
      const r = orig.apply(this, a);
      hook.call(this, a, r);
      return r;
    };
  };
  const sizes = new WeakMap();
  const registry = new FinalizationRegistry((bytes) => { G.liveBytesGc -= bytes; });
  const track = (obj, bytes) => {
    sizes.set(obj, { bytes, gone: false });
    G.liveBytes += bytes;
    G.liveBytesGc += bytes;
    G.peakLiveBytes = Math.max(G.peakLiveBytes, G.liveBytes);
    G.peakLiveBytesGc = Math.max(G.peakLiveBytesGc, G.liveBytesGc);
    registry.register(obj, bytes, obj);
  };
  const untrack = (obj) => {
    const s = sizes.get(obj);
    if (!s || s.gone) return;
    s.gone = true;
    G.liveBytes -= s.bytes;
    G.liveBytesGc -= s.bytes;
    registry.unregister(obj);
  };

  wrap(GPUDevice.prototype, 'createBuffer', (a, r) => { G.createBuffer++; G.createBufferBytes += a[0].size; track(r, a[0].size); });
  wrap(GPUDevice.prototype, 'createTexture', (a, r) => { const b = texBytes(a[0]); G.createTexture++; G.createTextureBytes += b; track(r, b); });
  wrap(GPUDevice.prototype, 'createBindGroup', () => { G.bindGroups++; });
  for (const n of ['createComputePipeline', 'createComputePipelineAsync']) wrap(GPUDevice.prototype, n, () => { G.computePipelines++; });
  for (const n of ['createRenderPipeline', 'createRenderPipelineAsync']) wrap(GPUDevice.prototype, n, () => { G.renderPipelines++; });
  wrap(GPUDevice.prototype, 'createCommandEncoder', () => { G.encoders++; });
  wrap(GPUBuffer.prototype, 'destroy', function () { untrack(this); });
  wrap(GPUTexture.prototype, 'destroy', function () { untrack(this); });
  wrap(GPUCommandEncoder.prototype, 'beginComputePass', () => { G.computePasses++; });
  wrap(GPUCommandEncoder.prototype, 'beginRenderPass', () => { G.renderPasses++; });
  wrap(GPUCommandEncoder.prototype, 'copyBufferToBuffer', () => { G.copyBufferToBuffer++; });
  wrap(GPUCommandEncoder.prototype, 'copyBufferToTexture', () => { G.copyBufferToTexture++; });
  wrap(GPUCommandEncoder.prototype, 'copyTextureToTexture', () => { G.copyTextureToTexture++; });
  wrap(GPUCommandEncoder.prototype, 'clearBuffer', () => { G.clearBuffer++; });
  wrap(GPUComputePassEncoder.prototype, 'dispatchWorkgroups', () => { G.dispatches++; });
  wrap(GPUQueue.prototype, 'writeBuffer', (a) => {
    G.writeBuffer++;
    const data = a[2];
    const el = data.BYTES_PER_ELEMENT || 1;
    G.writeBufferBytes += a[4] !== undefined ? a[4] * el : (data.byteLength ?? 0) - (a[3] || 0) * el;
  });
  wrap(GPUQueue.prototype, 'writeTexture', (a) => { G.writeTexture++; G.writeTextureBytes += a[1].byteLength ?? 0; });
  let sampling = false;
  wrap(GPUQueue.prototype, 'submit', function () {
    G.submits++;
    active();
    if (sampling) return;
    sampling = true;
    const t = now();
    this.onSubmittedWorkDone().then(
      () => { if (P.gpuDone.length < 2000) P.gpuDone.push(now() - t); sampling = false; },
      () => { sampling = false; },
    );
  });
  wrap(GPUCanvasContext.prototype, 'getCurrentTexture', () => { G.getCurrentTexture++; if (P.firstPresentAt === undefined) P.firstPresentAt = now(); });
})();
