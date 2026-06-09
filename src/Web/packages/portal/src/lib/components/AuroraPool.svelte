<script lang="ts">
  interface Props {
    textBlock?: HTMLElement | null;
  }
  let { textBlock = null }: Props = $props();

  import { sampleFlow } from "$lib/utils/aurora-noise";

  // ── Chip definitions ──────────────────────────────────────────────────────
  // hPos: left or right as % of container width. Preserved from the original
  // scattered layout so initial positions look like the current design.
  const CHIP_DEFS = [
    {
      id: "dexcom",
      file: "dexcom.png",
      name: "Dexcom",
      topPct: 8,
      hPos: { left: 44 },
    },
    {
      id: "loop",
      file: "loop.png",
      name: "Loop",
      topPct: 25,
      hPos: { left: 30 },
    },
    {
      id: "trio",
      file: "trio.jpg",
      name: "Trio",
      topPct: 30,
      hPos: { right: 30 },
    },
    {
      id: "libre",
      file: "libre.png",
      name: "FreeStyle Libre",
      topPct: 48,
      hPos: { left: 26 },
    },
    {
      id: "nightscout",
      file: "nightscout.png",
      name: "Nightscout",
      topPct: 44,
      hPos: { right: 26 },
    },
    {
      id: "tandem",
      file: "tandem.png",
      name: "Tandem",
      topPct: 70,
      hPos: { left: 20 },
    },
    {
      id: "omnipod",
      file: "omnipod.png",
      name: "Omnipod",
      topPct: 67,
      hPos: { right: 18 },
    },
    {
      id: "aaps",
      file: "aaps.png",
      name: "AAPS",
      topPct: 82,
      hPos: { right: 38 },
    },
    {
      id: "xdrip",
      file: "xdrip.jpg",
      name: "xDrip+",
      topPct: 84,
      hPos: { left: 8 },
    },
  ] as const;

  const INIT_ROTS = [-4, 6, -8, 5, -6, 7, -3, 9, -10] as const;

  // ── Physics constants ─────────────────────────────────────────────────────
  const BOB_FREQ_V = 0.7; // rad/s — vertical bob frequency
  const BOB_AMP_V = 10; // px/s² force amplitude (vertical)
  const BOB_FREQ_H = 0.45; // rad/s — horizontal drift frequency
  const BOB_AMP_H = 2; // px/s² force amplitude (horizontal)
  const LINEAR_DAMP = 0.03; // fraction of velocity lost per frame (not per second)
  const ANGULAR_DAMP = 0.07; // same for rotation
  const RESTITUTION = 0.2; // bounciness (0 = dead stop, 1 = perfectly elastic)
  const SPRING_K = 120; // drag spring constant (px/s² per px of offset)
  const MAX_DT = 0.05; // seconds — cap to prevent tunnelling on hidden tabs
  const ANG_KICK = 2; // deg/s angular impulse added on collision
  const TEXT_PAD = 1; // px padding added around text block collision rect
  const TIDE_AMP = 250; // px/s² — tidal force amplitude from aurora flow field
  const TOP_GUARD = 72; // px — keeps chips below the fixed nav header

  // ── Physics state (plain JS — NOT $state, no reactivity overhead) ─────────
  interface CS {
    cx: number;
    cy: number; // center position in container-local px
    vx: number;
    vy: number; // velocity px/s
    rot: number;
    rotV: number; // rotation deg, angular velocity deg/s
    phase: number; // bobbing phase offset (0–2π), unique per chip
    w: number;
    h: number; // measured pixel size (AABB half-extents: w/2, h/2)
    isDragged: boolean;
  }

  // containerEl is $state so $effect tracks it (triggers after bind:this fires)
  let containerEl: HTMLDivElement | null = $state(null);

  // chipElRefs is plain — populated synchronously by the assignRef action before $effect runs
  const chipElRefs: (HTMLElement | null)[] = Array(CHIP_DEFS.length).fill(null);

  const cs: CS[] = CHIP_DEFS.map((_, i) => ({
    cx: 0,
    cy: 0,
    vx: 0,
    vy: 0,
    rot: INIT_ROTS[i],
    rotV: 0,
    phase: (i / CHIP_DEFS.length) * Math.PI * 2,
    w: 60,
    h: 30,
    isDragged: false,
  }));

  let lastTime = 0;
  let rafId = 0;
  let pointer = { x: 0, y: 0 };
  let grabOffset = { x: 0, y: 0 }; // pointer-to-center offset at grab time
  let dragIdx = -1;
  let textRects: { x: number; y: number; w: number; h: number }[] = [];

  // ── Svelte action: collect chip element refs without triggering reactivity ──
  function assignRef(el: HTMLElement, i: number) {
    chipElRefs[i] = el;
    return {
      destroy() {
        chipElRefs[i] = null;
      },
    };
  }

  // ── Measurement helpers ───────────────────────────────────────────────────
  function measureSizes() {
    chipElRefs.forEach((el, i) => {
      if (!el) return;
      const r = el.getBoundingClientRect();
      cs[i].w = r.width;
      cs[i].h = r.height;
    });
  }

  function initPositions() {
    if (!containerEl) return;
    const cw = containerEl.offsetWidth;
    const ch = containerEl.offsetHeight;
    CHIP_DEFS.forEach((def, i) => {
      cs[i].cy = ch * (def.topPct / 100);
      cs[i].cx =
        "left" in def.hPos
          ? cw * ((def.hPos as { left: number }).left / 100)
          : cw * (1 - (def.hPos as { right: number }).right / 100);
    });
    applyTransforms();
  }

  function measureTextRect() {
    if (!containerEl || !textBlock) {
      textRects = [];
      return;
    }
    const cr = containerEl.getBoundingClientRect();
    textRects = Array.from(textBlock.children).map((child) => {
      const tr = child.getBoundingClientRect();
      return {
        x: tr.left - cr.left - TEXT_PAD,
        y: tr.top - cr.top - TEXT_PAD,
        w: tr.width + TEXT_PAD * 2,
        h: tr.height + TEXT_PAD * 2,
      };
    });
  }

  // ── Collision functions ───────────────────────────────────────────────────
  // ── Capsule helpers ────────────────────────────────────────────────────────
  // Each pill is a capsule: a line segment with radius h/2.
  // The segment runs from (-ext, 0) to (ext, 0) in local space, rotated by c.rot.
  function capsuleEndpoints(c: CS): [number, number, number, number] {
    const ext = Math.max((c.w - c.h) / 2, 0);
    const rad = (c.rot * Math.PI) / 180;
    const cos = Math.cos(rad),
      sin = Math.sin(rad);
    return [
      c.cx - cos * ext,
      c.cy - sin * ext, // endpoint A
      c.cx + cos * ext,
      c.cy + sin * ext, // endpoint B
    ];
  }

  function capsuleRadius(c: CS): number {
    return c.h / 2;
  }

  // Closest point on segment (px,py)-(qx,qy) to point (x,y)
  function closestOnSeg(
    px: number,
    py: number,
    qx: number,
    qy: number,
    x: number,
    y: number,
  ): [number, number] {
    const dx = qx - px,
      dy = qy - py;
    const len2 = dx * dx + dy * dy;
    if (len2 < 1e-8) return [px, py];
    const t = Math.max(0, Math.min(1, ((x - px) * dx + (y - py) * dy) / len2));
    return [px + t * dx, py + t * dy];
  }

  // Closest distance between two segments, returns closest points + dist²
  function segSegClosest(
    a0x: number,
    a0y: number,
    a1x: number,
    a1y: number,
    b0x: number,
    b0y: number,
    b1x: number,
    b1y: number,
  ): { cx: number; cy: number; dx: number; dy: number; dist2: number } {
    // Sample all four endpoint-to-segment projections and pick the closest pair
    let bestD2 = Infinity,
      bestCx = 0,
      bestCy = 0,
      bestDx = 0,
      bestDy = 0;
    const check = (cx: number, cy: number, dx: number, dy: number) => {
      const d2 = (dx - cx) * (dx - cx) + (dy - cy) * (dy - cy);
      if (d2 < bestD2) {
        bestD2 = d2;
        bestCx = cx;
        bestCy = cy;
        bestDx = dx;
        bestDy = dy;
      }
    };
    let [cx, cy] = closestOnSeg(b0x, b0y, b1x, b1y, a0x, a0y);
    check(a0x, a0y, cx, cy);
    [cx, cy] = closestOnSeg(b0x, b0y, b1x, b1y, a1x, a1y);
    check(a1x, a1y, cx, cy);
    [cx, cy] = closestOnSeg(a0x, a0y, a1x, a1y, b0x, b0y);
    check(cx, cy, b0x, b0y);
    [cx, cy] = closestOnSeg(a0x, a0y, a1x, a1y, b1x, b1y);
    check(cx, cy, b1x, b1y);
    return { cx: bestCx, cy: bestCy, dx: bestDx, dy: bestDy, dist2: bestD2 };
  }

  // ── Collision functions ───────────────────────────────────────────────────
  function wallCollide(c: CS) {
    if (!containerEl) return;
    const cw = containerEl.offsetWidth;
    const ch = containerEl.offsetHeight;
    const r = capsuleRadius(c);
    const [ax, ay, bx, by] = capsuleEndpoints(c);

    // Check each endpoint against walls
    const minX = Math.min(ax, bx) - r;
    const maxX = Math.max(ax, bx) + r;
    const minY = Math.min(ay, by) - r;
    const maxY = Math.max(ay, by) + r;

    if (minX < 0) {
      c.cx -= minX;
      c.vx = Math.abs(c.vx) * RESTITUTION;
      c.rotV += ANG_KICK * (Math.random() * 2 - 1);
    }
    if (maxX > cw) {
      c.cx -= maxX - cw;
      c.vx = -Math.abs(c.vx) * RESTITUTION;
      c.rotV += ANG_KICK * (Math.random() * 2 - 1);
    }
    if (minY < TOP_GUARD) {
      c.cy += TOP_GUARD - minY;
      c.vy = Math.abs(c.vy) * RESTITUTION;
      c.rotV += ANG_KICK * (Math.random() * 2 - 1);
    }
    if (maxY > ch) {
      c.cy -= maxY - ch;
      c.vy = -Math.abs(c.vy) * RESTITUTION;
      c.rotV += ANG_KICK * (Math.random() * 2 - 1);
    }
  }

  function chipCollide(a: CS, b: CS) {
    const [a0x, a0y, a1x, a1y] = capsuleEndpoints(a);
    const [b0x, b0y, b1x, b1y] = capsuleEndpoints(b);
    const minD = capsuleRadius(a) + capsuleRadius(b);

    const {
      cx: pax,
      cy: pay,
      dx: pbx,
      dy: pby,
      dist2,
    } = segSegClosest(a0x, a0y, a1x, a1y, b0x, b0y, b1x, b1y);
    if (dist2 >= minD * minD || dist2 < 1e-6) return;

    const dist = Math.sqrt(dist2);
    const nx = (pbx - pax) / dist;
    const ny = (pby - pay) / dist;
    const overlap = minD - dist;

    if (!a.isDragged) {
      a.cx -= nx * overlap * 0.5;
      a.cy -= ny * overlap * 0.5;
    }
    if (!b.isDragged) {
      b.cx += nx * overlap * 0.5;
      b.cy += ny * overlap * 0.5;
    }

    const relV = (b.vx - a.vx) * nx + (b.vy - a.vy) * ny;
    if (relV > 0) return;

    const j = (-(1 + RESTITUTION) * relV) / 2;
    if (!a.isDragged) {
      a.vx -= j * nx;
      a.vy -= j * ny;
      a.rotV += ANG_KICK * Math.sign(a.vx || 1);
    }
    if (!b.isDragged) {
      b.vx += j * nx;
      b.vy += j * ny;
      b.rotV += ANG_KICK * Math.sign(b.vx || 1);
    }
  }

  function textCollide(c: CS) {
    if (textRects.length === 0) return;
    const r = capsuleRadius(c);
    const [ax, ay, bx, by] = capsuleEndpoints(c);
    const probes = [
      [ax, ay],
      [bx, by],
      [c.cx, c.cy],
    ] as const;

    for (const rect of textRects) {
      for (const [ex, ey] of probes) {
        const clampX = Math.max(rect.x, Math.min(rect.x + rect.w, ex));
        const clampY = Math.max(rect.y, Math.min(rect.y + rect.h, ey));
        const dx = ex - clampX;
        const dy = ey - clampY;
        const dist2 = dx * dx + dy * dy;
        if (dist2 >= r * r || dist2 < 1e-6) continue;

        const dist = Math.sqrt(dist2);
        const nx = dx / dist;
        const ny = dy / dist;
        const push = r - dist;

        c.cx += nx * push;
        c.cy += ny * push;

        const dot = c.vx * nx + c.vy * ny;
        if (dot < 0) {
          c.vx -= (1 + RESTITUTION) * dot * nx;
          c.vy -= (1 + RESTITUTION) * dot * ny;
        }
        c.rotV += ANG_KICK * (Math.random() * 2 - 1);
        return; // resolve one contact per frame
      }
    }
  }

  // ── Render ────────────────────────────────────────────────────────────────
  function applyTransforms() {
    chipElRefs.forEach((el, i) => {
      if (!el) return;
      const c = cs[i];
      // translate positions the top-left corner; transform-origin rotates around center
      el.style.transform = `translate(${c.cx - c.w / 2}px, ${c.cy - c.h / 2}px) rotate(${c.rot}deg)`;
      el.style.transformOrigin = `${c.w / 2}px ${c.h / 2}px`;
    });
  }

  // ── Physics loop ──────────────────────────────────────────────────────────
  function tick(now: number) {
    const dt = Math.min((now - lastTime) / 1000, MAX_DT);
    lastTime = now;
    const t = now / 1000;
    const cw = containerEl ? containerEl.offsetWidth : 0;
    const ch = containerEl ? containerEl.offsetHeight : 0;

    // Integrate forces
    for (let i = 0; i < cs.length; i++) {
      const c = cs[i];
      if (c.isDragged) {
        // Spring pull toward grab-adjusted pointer position
        // (grabOffset keeps the chip stationary at pickup, no snap)
        c.vx += (pointer.x - grabOffset.x - c.cx) * SPRING_K * dt;
        c.vy += (pointer.y - grabOffset.y - c.cy) * SPRING_K * dt;
      } else {
        // Sine-wave bob forces — unique phase per chip so they desync naturally
        c.vy += Math.sin(t * BOB_FREQ_V + c.phase) * BOB_AMP_V * dt;
        c.vx += Math.sin(t * BOB_FREQ_H + c.phase * 1.3) * BOB_AMP_H * dt;
        // Tidal force — flow vector from the same aurora noise field
        if (cw > 0 && ch > 0) {
          const flow = sampleFlow(c.cx, c.cy, cw, ch, t);
          c.vx += (flow.rx - 0.5) * 2 * TIDE_AMP * dt;
          c.vy += (flow.ry - 0.5) * 2 * TIDE_AMP * dt;
        }
      }
      c.vx *= 1 - LINEAR_DAMP;
      c.vy *= 1 - LINEAR_DAMP;
      c.rotV *= 1 - ANGULAR_DAMP;
      c.cx += c.vx * dt;
      c.cy += c.vy * dt;
      c.rot += c.rotV * dt;
    }

    // Collisions
    for (let i = 0; i < cs.length; i++)
      if (!cs[i].isDragged) wallCollide(cs[i]);
    for (let i = 0; i < cs.length - 1; i++)
      for (let j = i + 1; j < cs.length; j++) chipCollide(cs[i], cs[j]);
    for (let i = 0; i < cs.length; i++)
      if (!cs[i].isDragged) textCollide(cs[i]);

    applyTransforms();
    rafId = requestAnimationFrame(tick);
  }

  // ── Pointer / drag handlers ───────────────────────────────────────────────
  function onPointerDown(e: PointerEvent, i: number) {
    e.stopPropagation();
    dragIdx = i;
    cs[i].isDragged = true;
    cs[i].vx = 0;
    cs[i].vy = 0;
    if (containerEl) {
      const cr = containerEl.getBoundingClientRect();
      pointer = { x: e.clientX - cr.left, y: e.clientY - cr.top };
      // Record offset from pointer to chip center so the chip doesn't snap
      grabOffset = { x: pointer.x - cs[i].cx, y: pointer.y - cs[i].cy };
    }
    containerEl?.setPointerCapture(e.pointerId);
  }

  function onPointerMove(e: PointerEvent) {
    if (dragIdx < 0 || !containerEl) return;
    const cr = containerEl.getBoundingClientRect();
    pointer = { x: e.clientX - cr.left, y: e.clientY - cr.top };
  }

  function onPointerUp(_e: PointerEvent) {
    if (dragIdx >= 0) {
      cs[dragIdx].isDragged = false;
      dragIdx = -1;
    }
  }

  // ── Lifecycle ─────────────────────────────────────────────────────────────
  // Physics init — runs once when containerEl is set (bind:this fires on mount)
  $effect(() => {
    if (!containerEl) return;
    measureSizes();
    initPositions();
    lastTime = performance.now();
    rafId = requestAnimationFrame(tick);
    const ro = new ResizeObserver(() => measureTextRect());
    ro.observe(containerEl);
    return () => {
      cancelAnimationFrame(rafId);
      ro.disconnect();
    };
  });

  // Text rect — re-measures whenever the textBlock prop changes
  // (parent's bind:this fires after its own mount, after this effect)
  $effect(() => {
    measureTextRect();
  });
</script>

<div
  class="absolute inset-0 overflow-hidden"
  aria-hidden="true"
  bind:this={containerEl}
  onpointermove={onPointerMove}
  onpointerup={onPointerUp}
>
  <!-- Mobile: static chip wrap -->
  <div class="md:hidden absolute top-[72px] left-0 right-0 px-4">
    <div class="flex flex-wrap gap-x-1.5 gap-y-2 justify-center">
      {#each CHIP_DEFS as def}
        <div
          class="flex items-center gap-1.5 bg-[oklch(0.10_0.028_261/85%)] border border-[oklch(1_0_0/20%)] rounded-full py-0.5 pr-2 pl-0.5 backdrop-blur-sm max-w-full"
        >
          <img
            src="/logos/{def.file}"
            alt=""
            class="size-3.5 rounded object-cover shrink-0"
          />
          <span class="text-[10px] font-semibold text-white whitespace-nowrap"
            >{def.name}</span
          >
        </div>
      {/each}
    </div>
  </div>

  <!-- Desktop: physics-driven chips -->
  <!--
        Each chip starts at top:0 left:0 and is repositioned entirely by
        applyTransforms() via el.style.transform each rAF frame.
        pointer-events-auto + touch-none lets pointer events through despite
        the aria-hidden parent being pointer-events-none in the original.
    -->
  {#each CHIP_DEFS as def, i}
    <div
      class="hidden md:flex absolute top-0 left-0 items-center gap-[7px]
                   bg-[oklch(0.10_0.028_261/85%)] border border-[oklch(1_0_0/20%)]
                   rounded-full py-[5px] pr-3 pl-1.5 backdrop-blur-[6px] mix-blend-screen
                   pointer-events-auto cursor-grab active:cursor-grabbing select-none touch-none"
      use:assignRef={i}
      onpointerdown={(e) => onPointerDown(e, i)}
      onpointerup={onPointerUp}
    >
      <img src="/logos/{def.file}" alt="" class="size-5 rounded object-cover" />
      <span class="text-[11px] font-semibold text-white whitespace-nowrap"
        >{def.name}</span
      >
    </div>
  {/each}
</div>
