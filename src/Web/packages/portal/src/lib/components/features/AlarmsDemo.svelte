<script lang="ts">
    // Stripped-down replay demo — real playback engine + chart, synthetic data,
    // no API calls. Wire to the real ReplayPanel once the portal auth story is done.

    import { onDestroy } from "svelte";
    import { Play, Pause, RotateCcw, ChevronRight, Droplet, Timer } from "@lucide/svelte";
    import * as Collapsible from "@nocturne/ui/ui/collapsible";
    import { Switch } from "@nocturne/ui/ui/switch";

    // ── Inlined helpers (from app/alerts/severity.ts + alertTime.ts) ──────────
    function severityVar(s: string | undefined): string {
        if (s === "critical") return "var(--status-critical)";
        if (s === "warning")  return "var(--status-warning)";
        if (s === "info")     return "var(--status-info)";
        return "var(--muted-foreground)";
    }
    function severityLabel(s: string | undefined): string {
        if (s === "critical") return "Critical";
        if (s === "warning")  return "Warning";
        if (s === "info")     return "Info";
        return s ?? "";
    }
    function severityChip(s: string | undefined): string {
        if (s === "critical") return "bg-status-critical/15 text-status-critical";
        if (s === "warning")  return "bg-status-warning/15 text-status-warning";
        if (s === "info")     return "bg-status-info/15 text-status-info";
        return "bg-muted text-muted-foreground";
    }
    function formatTime(d: Date): string {
        try { return new Intl.DateTimeFormat(undefined, { hour: "2-digit", minute: "2-digit" }).format(d); }
        catch { return d.toISOString().slice(11, 16); }
    }

    // ── Synthetic overnight window: Mar 15 22:00 → Mar 16 06:00 ─────────────
    const BASE_MS = new Date("2026-03-15T22:00:00").getTime();
    const SPAN_MS = 8 * 3600_000;

    // Synthetic rules — each has a single leaf condition for the sidebar.
    // BG first crosses 180 at 4.2875 h (interpolated from waypoints [4.1,162]→[4.35,186]).
    // Sustained 30-min alarm fires 0.5 h later at 4.7875 h.
    const R1_FIRED_MS    = BASE_MS + 2.25   * 3600_000;
    const R1_RESOLVED_MS = BASE_MS + 2.92   * 3600_000;
    const R2_CROSS_MS    = BASE_MS + 4.2875 * 3600_000; // BG first > 180
    const R2_FIRED_MS    = R2_CROSS_MS + 30 * 60_000;   // 30 min sustained

    const RULES = [
        {
            id: "r1",
            name: "BG drops below 70",
            severity: "critical",
            leaf: { label: "BG < 70 mg/dL", kind: "threshold", sustained: false },
            firedMs:    R1_FIRED_MS,
            resolvedMs: R1_RESOLVED_MS,
        },
        {
            id: "r2",
            name: "Stuck above 180 for 30 min",
            severity: "warning",
            leaf: { label: "BG > 180 mg/dL for 30 min", kind: "sustained", sustained: true },
            firedMs:    R2_FIRED_MS,
            resolvedMs: null,
        },
    ] as const;

    const EVENTS = [
        { ruleId: "r1", tMs: R1_FIRED_MS,    ruleName: RULES[0].name, severity: "critical", kind: "fired"         },
        { ruleId: "r1", tMs: R1_RESOLVED_MS,  ruleName: RULES[0].name, severity: "critical", kind: "auto_resolved" },
        { ruleId: "r2", tMs: R2_FIRED_MS,     ruleName: RULES[1].name, severity: "warning",  kind: "fired"         },
    ] as const;

    // ── Glucose curve ─────────────────────────────────────────────────────────
    const WAYPOINTS: readonly [number, number][] = [
        [0,    140], [0.75, 132], [1.25, 118], [1.75,  88], [2,   76],
        [2.25,  65], [2.5,   60], [2.75,  68], [2.92,  73],
        [3.25, 100], [3.75, 132], [4.1,  162], [4.35, 186],
        [4.75, 212], [5.25, 218], [5.75, 198], [6.5,  179], [7.25, 164], [8, 158],
    ] as const;

    function lerp(a: number, b: number, t: number): number { return a + (b - a) * t; }
    function interpGlucose(h: number): number {
        for (let i = 1; i < WAYPOINTS.length; i++) {
            const [h0, g0] = WAYPOINTS[i - 1];
            const [h1, g1] = WAYPOINTS[i];
            if (h <= h1) return lerp(g0, g1, (h - h0) / (h1 - h0));
        }
        return WAYPOINTS[WAYPOINTS.length - 1][1];
    }

    const SVG_W = 400, SVG_H = 140;
    const Y_MIN = 40,  Y_MAX = 300;
    function toX(h: number): number { return (h / 8) * SVG_W; }
    function toY(g: number): number { return SVG_H - ((g - Y_MIN) / (Y_MAX - Y_MIN)) * SVG_H; }
    function sgvColor(g: number): string {
        if (g < 54)   return "var(--glucose-very-low)";
        if (g < 70)   return "var(--glucose-low)";
        if (g <= 180) return "var(--glucose-in-range)";
        if (g <= 250) return "var(--glucose-high)";
        return "var(--glucose-very-high)";
    }

    interface GPt { h: number; sgv: number; x: number; y: number; color: string; }
    const PTS: GPt[] = Array.from({ length: 97 }, (_, i) => {
        const h = (i / 96) * 8;
        const sgv = interpGlucose(h);
        return { h, sgv, x: toX(h), y: toY(sgv), color: sgvColor(sgv) };
    });

    interface Seg { color: string; pts: GPt[]; }
    const SEGS: Seg[] = [];
    for (const pt of PTS) {
        const last = SEGS[SEGS.length - 1];
        if (last && last.color === pt.color) {
            last.pts.push(pt);
        } else {
            const overlap = last ? [last.pts[last.pts.length - 1]] : [];
            SEGS.push({ color: pt.color, pts: [...overlap, pt] });
        }
    }

    const yLow  = toY(70);
    const yHigh = toY(180);
    const CHART_EVENTS = EVENTS.map(ev => ({ ...ev, x: toX((ev.tMs - BASE_MS) / 3600_000) }));

    // ── Playback engine ───────────────────────────────────────────────────────
    interface Props { height?: number; }
    let { height = 400 }: Props = $props();

    const BASE_ANIM_MS = 14_000;
    let speed   = $state(1);
    let playPct = $state(0);
    let maxPct  = $state(0);
    let playing = $state(false);
    let rafId: number | null = null;
    let lastTs: number | null = null;

    function tick(ts: number): void {
        if (!playing) { rafId = null; return; }
        if (lastTs == null) lastTs = ts;
        const next = Math.min(100, playPct + ((ts - lastTs) / (BASE_ANIM_MS / speed)) * 100);
        lastTs = ts; playPct = next;
        if (next > maxPct) maxPct = next;
        if (next >= 100) { playing = false; rafId = null; lastTs = null; return; }
        rafId = requestAnimationFrame(tick);
    }

    function play(): void {
        if (playing) return;
        if (playPct >= 100) { playPct = 0; maxPct = 0; }
        playing = true; lastTs = null;
        rafId = requestAnimationFrame(tick);
    }
    function pause(): void {
        playing = false;
        if (rafId != null) cancelAnimationFrame(rafId);
        rafId = null; lastTs = null;
    }
    function toggle(): void { if (playing) pause(); else play(); }
    function reset(): void  { pause(); playPct = 0; maxPct = 0; }
    function seek(pct: number): void {
        pause();
        playPct = Math.max(0, Math.min(100, pct));
        if (playPct > maxPct) maxPct = playPct;
    }

    onDestroy(() => pause());
    $effect(() => { play(); });

    const currentMs    = $derived(BASE_MS + (SPAN_MS * playPct) / 100);
    const currentDate  = $derived(new Date(currentMs));
    const playheadX    = $derived(toX((currentMs - BASE_MS) / 3600_000));
    const currentGlucose = $derived(interpGlucose((currentMs - BASE_MS) / 3600_000));

    // Per-rule live state at the playhead
    let disabledRuleIds = $state(new Set<string>());

    const ruleStates = $derived(
        RULES.map(r => {
            const disabled = disabledRuleIds.has(r.id);
            const fired    = !disabled && r.firedMs <= currentMs;
            const resolved = r.resolvedMs != null && r.resolvedMs <= currentMs;
            const active   = fired && !resolved;
            // Leaf truth:
            //   threshold — true while BG is currently below 70
            //   sustained — true once BG has been above 180 for 30 min (i.e. past fire time)
            //                but the inner condition (BG > 180) must still hold
            const leafTrue = disabled ? false
                : r.leaf.kind === "threshold"
                    ? currentGlucose < 70
                    : currentMs >= R2_FIRED_MS && currentGlucose > 180;
            const currentValue = r.leaf.kind === "threshold"
                ? `${Math.round(currentGlucose)} mg/dL`
                : null;
            return { ...r, disabled, active, leafTrue, currentValue };
        })
    );

    // Events and ticks filtered by disabled rules
    const firedEvents = $derived(
        EVENTS.filter(ev => ev.tMs <= currentMs && !disabledRuleIds.has(ev.ruleId))
    );

    const visibleChartEvents = $derived(
        CHART_EVENTS.filter(ev => !disabledRuleIds.has(ev.ruleId))
    );

    const STRIP_TICKS = EVENTS.map(ev => ({
        ...ev,
        xPct: ((ev.tMs - BASE_MS) / SPAN_MS) * 100,
    }));

    const visibleStripTicks = $derived(
        STRIP_TICKS.filter(t => !disabledRuleIds.has(t.ruleId))
    );

    function handleStripPointer(e: PointerEvent): void {
        const r = (e.currentTarget as SVGSVGElement).getBoundingClientRect();
        seek(Math.max(0, Math.min(100, ((e.clientX - r.left) / r.width) * 100)));
    }
    function handleSpeedChange(e: Event): void {
        const v = Number((e.target as HTMLSelectElement).value);
        if (Number.isFinite(v)) speed = v;
    }
</script>


<div
    class="rounded-[14px] border border-white/10 bg-[oklch(0.10_0.025_261)] flex flex-col gap-0 overflow-hidden"
    style:max-height="{height}px"
>
    <!-- Stacked: rules → chart+playback → events log -->
    <div class="flex-1 min-h-0 flex flex-col overflow-hidden">

        <!-- Rules sidebar -->
        <div class="shrink-0 overflow-y-auto border-b border-white/8 p-2" data-testid="rule-sidebar">
            <div class="font-mono text-[10px] tracking-widest uppercase text-muted-foreground/60 px-1 pb-1.5">Rules</div>
            {#each ruleStates as r (r.id)}
                <Collapsible.Root open class="rounded-md border bg-background mb-1.5">
                    <div class="flex items-center gap-2 px-2 py-1.5">
                        <Collapsible.Trigger class="group flex flex-1 min-w-0 items-center gap-2 text-left text-sm">
                            <ChevronRight class="h-3.5 w-3.5 shrink-0 text-muted-foreground transition-transform group-data-[state=open]:rotate-90" />
                            <span
                                data-testid="rule-status-pip"
                                class="inline-block h-2.5 w-2.5 shrink-0 rounded-full transition-colors duration-200"
                                class:opacity-50={r.disabled}
                                style:background-color={r.active ? severityVar(r.severity) : "transparent"}
                                style:border="1.5px solid {severityVar(r.severity)}"
                                aria-hidden="true"
                            ></span>
                            <span class="flex-1 min-w-0 truncate">{r.name}</span>
                            <span class="shrink-0 rounded px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide {severityChip(r.severity)}">
                                {r.disabled ? "Off" : "On"}
                            </span>
                        </Collapsible.Trigger>
                        <Switch
                            checked={!r.disabled}
                            onCheckedChange={(c) => {
                                const next = new Set(disabledRuleIds);
                                if (c) next.delete(r.id); else next.add(r.id);
                                disabledRuleIds = next;
                            }}
                            aria-label={r.disabled ? `Enable ${r.name}` : `Disable ${r.name}`}
                        />
                    </div>
                    <Collapsible.Content class="border-t px-2 py-1.5">
                        <ul class="space-y-1">
                            <li data-testid="rule-leaf" class="flex items-center gap-2 text-xs" class:opacity-30={r.disabled}>
                                <span
                                    class="inline-block h-2 w-2 shrink-0 rounded-full transition-colors duration-200"
                                    class:bg-status-normal={r.leafTrue}
                                    class:opacity-30={!r.leafTrue}
                                    style:background={r.leafTrue ? undefined : "var(--muted-foreground)"}
                                    aria-hidden="true"
                                ></span>
                                <span class="grid h-5 w-5 shrink-0 place-items-center rounded bg-blue-500/10 text-blue-400" aria-hidden="true">
                                    <Droplet class="h-3 w-3" />
                                </span>
                                {#if r.leaf.sustained}
                                    <Timer class="h-3 w-3 shrink-0 text-muted-foreground" aria-hidden="true" />
                                {/if}
                                <span class="flex-1 min-w-0 truncate">
                                    {r.leaf.label}
                                    {#if r.currentValue}
                                        <span class="ml-1 text-muted-foreground tabular-nums">· {r.currentValue}</span>
                                    {/if}
                                </span>
                            </li>
                        </ul>
                    </Collapsible.Content>
                </Collapsible.Root>
            {/each}
        </div>

        <!-- Chart + playback + events log -->
        <div class="flex-1 min-h-0 flex flex-col gap-0 overflow-hidden">

            <!-- Glucose chart -->
            <div class="px-3 pt-2 shrink-0">
                <svg
                    viewBox="0 0 {SVG_W} {SVG_H}"
                    class="w-full rounded-lg overflow-hidden"
                    style:height="120px"
                    preserveAspectRatio="none"
                    aria-hidden="true"
                >
                    <rect width={SVG_W} height={SVG_H} fill="oklch(0.14 0.025 261)" />
                    <rect x="0" y={yHigh} width={SVG_W} height={yLow - yHigh} fill="oklch(0.6 0.118 184.704 / 0.06)" />
                    <line x1="0" y1={yLow}  x2={SVG_W} y2={yLow}  stroke="var(--glucose-low)"  stroke-width="0.8" stroke-dasharray="4 3" opacity="0.5" />
                    <line x1="0" y1={yHigh} x2={SVG_W} y2={yHigh} stroke="var(--glucose-high)" stroke-width="0.8" stroke-dasharray="4 3" opacity="0.5" />
                    {#each SEGS as seg (seg.color + (seg.pts[0]?.h ?? 0))}
                        <polyline
                            points={seg.pts.map(p => `${p.x},${p.y}`).join(" ")}
                            fill="none" stroke={seg.color} stroke-width="2"
                            stroke-linecap="round" stroke-linejoin="round"
                        />
                    {/each}
                    {#each visibleChartEvents as ev (`${ev.kind}-${ev.tMs}`)}
                        {@const dimmed = ev.tMs > currentMs}
                        <line x1={ev.x} x2={ev.x} y1="0" y2={SVG_H}
                              stroke={severityVar(ev.severity)} stroke-width="1.5"
                              stroke-dasharray={ev.kind === "auto_resolved" ? "3 3" : null}
                              opacity={dimmed ? 0.2 : 0.9} />
                        {#if !dimmed}
                            <circle cx={ev.x} cy={ev.kind === "auto_resolved" ? yLow - 4 : yHigh + 4}
                                    r="3.5" fill={severityVar(ev.severity)} opacity="0.95" />
                        {/if}
                    {/each}
                    <line x1={playheadX} x2={playheadX} y1="0" y2={SVG_H}
                          stroke="oklch(0.97 0.005 261)" stroke-width="1.5" opacity="0.7" />
                </svg>
            </div>

            <!-- Playback strip -->
            <div class="px-3 pt-2 shrink-0">
                <div class="flex items-center gap-2">
                    <button type="button" onclick={toggle}
                        class="size-7 rounded-md border border-white/15 bg-white/5 hover:bg-white/10 flex items-center justify-center shrink-0 transition-colors"
                        aria-label={playing ? "Pause" : "Play"}>
                        {#if playing}<Pause class="size-3.5 text-foreground/80" />{:else}<Play class="size-3.5 text-foreground/80" />{/if}
                    </button>
                    <button type="button" onclick={reset}
                        class="size-7 rounded-md border border-white/15 bg-white/5 hover:bg-white/10 flex items-center justify-center shrink-0 transition-colors"
                        aria-label="Reset">
                        <RotateCcw class="size-3.5 text-foreground/80" />
                    </button>
                    <select value={String(speed)} onchange={handleSpeedChange}
                        class="h-7 rounded-md border border-white/15 bg-white/5 px-2 text-[12px] text-foreground/80 shrink-0 w-16"
                        aria-label="Playback speed">
                        {#each [0.25, 0.5, 1, 2] as opt (opt)}
                            <option value={String(opt)} selected={speed === opt}>{opt}x</option>
                        {/each}
                    </select>
                    <svg role="presentation"
                        class="h-7 flex-1 cursor-pointer rounded border border-white/10 bg-white/4"
                        viewBox="0 0 100 28" preserveAspectRatio="none"
                        onpointerdown={handleStripPointer}>
                        <rect x="0" y="0" width={maxPct} height="28" fill="oklch(1 0 0 / 0.06)" />
                        <line x1={playPct} x2={playPct} y1="0" y2="28"
                              vector-effect="non-scaling-stroke"
                              stroke="oklch(0.97 0.005 261 / 0.8)" stroke-width="1.5" />
                        {#each visibleStripTicks as tick (`${tick.kind}-${tick.tMs}`)}
                            {@const dimmed = tick.xPct > playPct}
                            {@const isResolved = tick.kind === "auto_resolved"}
                            <line x1={tick.xPct} x2={tick.xPct}
                                  y1={isResolved ? "8" : "20"} y2="28"
                                  vector-effect="non-scaling-stroke"
                                  stroke={severityVar(tick.severity)} stroke-width="2"
                                  stroke-dasharray={isResolved ? "2 2" : null}
                                  opacity={dimmed ? 0.25 : 1} />
                        {/each}
                    </svg>
                    <span class="font-mono text-[11px] text-muted-foreground tabular-nums shrink-0 w-[68px] text-right">
                        {formatTime(currentDate)}
                    </span>
                </div>
            </div>

            <!-- Events log — fixed height so new entries scroll in without shifting layout -->
            <div class="h-24 shrink-0 overflow-y-auto mx-3 mt-2 mb-3 rounded-md border divide-y text-sm">
                {#if firedEvents.length === 0}
                    <div class="flex items-center justify-center h-full text-xs text-muted-foreground/50 px-3 py-4">
                        No events yet — playhead at start of window.
                    </div>
                {:else}
                    {#each firedEvents as ev (`${ev.kind}-${ev.tMs}`)}
                        {@const isResolved = ev.kind === "auto_resolved"}
                        <div class="flex items-center gap-3 px-3 py-2">
                            <span class="size-2 rounded-full shrink-0"
                                  style:background={isResolved ? "var(--glucose-in-range)" : severityVar(ev.severity)}></span>
                            <span class="font-mono text-xs text-muted-foreground tabular-nums w-12 shrink-0">
                                {formatTime(new Date(ev.tMs))}
                            </span>
                            <span class="shrink-0 rounded px-1.5 py-0.5 text-[11px] font-semibold {severityChip(ev.severity)}"
                                  style:color={isResolved ? "var(--glucose-in-range)" : undefined}>
                                {isResolved ? "Resolved" : severityLabel(ev.severity)}
                            </span>
                            <span class="flex-1 min-w-0 truncate">{ev.ruleName}</span>
                        </div>
                    {/each}
                {/if}
            </div>

        </div>
    </div>
</div>
