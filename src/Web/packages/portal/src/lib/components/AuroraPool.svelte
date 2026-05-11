<script lang="ts">
    // Decorative floating layer — mini chart thumbnails + connector chips
    // positioned absolutely over the aurora canvas.

    const charts = [
        { top: "11%", left: "6%", width: 230, rot: -8, kind: "wave", label: "OVERNIGHT · 7d" },
        { top: "62%", left: "9%", width: 195, rot: 6, kind: "agp", label: "AGP · 14d" },
        { top: "18%", right: "7%", width: 215, rot: 9, kind: "climb", label: "POST-LUNCH · 30d" },
        { top: "55%", right: "10%", width: 185, rot: -7, kind: "steady", label: "WEEK · TIR 70%" },
        { top: "78%", left: "44%", width: 240, rot: 3, kind: "crest", label: "DAWN · 14d" },
    ] as const;

    const chips = [
        { id: "dexcom", file: "dexcom.png", name: "Dexcom", top: "8%", left: "44%", rot: -4 },
        { id: "loop", file: "loop.png", name: "Loop", top: "25%", left: "30%", rot: 6 },
        { id: "trio", file: "trio.jpg", name: "Trio", top: "30%", right: "30%", rot: -8 },
        { id: "libre", file: "libre.png", name: "FreeStyle Libre", top: "48%", left: "26%", rot: 5 },
        { id: "nightscout", file: "nightscout.png", name: "Nightscout", top: "44%", right: "26%", rot: -6 },
        { id: "tandem", file: "tandem.png", name: "Tandem", top: "70%", left: "20%", rot: 7 },
        { id: "omnipod", file: "omnipod.png", name: "Omnipod", top: "67%", right: "18%", rot: -3 },
        { id: "aaps", file: "aaps.png", name: "AAPS", top: "82%", right: "38%", rot: 9 },
        { id: "xdrip", file: "xdrip.jpg", name: "xDrip+", top: "84%", left: "8%", rot: -10 },
    ] as const;

    const N = 56;
    const W = 100;
    const H = 38;

    function buildPath(kind: string): { line: string; fill: string } {
        const pts: number[] = [];
        for (let i = 0; i < N; i++) {
            const t = i / (N - 1);
            let v: number;
            if (kind === "wave") v = 110 + 22 * Math.sin(t * 7) + 8 * Math.cos(t * 17);
            else if (kind === "climb") v = 90 + t * 90 + 6 * Math.sin(t * 9);
            else if (kind === "agp") v = 130 + 50 * Math.sin(t * Math.PI * 2 - 0.6) + 4 * Math.sin(t * 13);
            else if (kind === "crest") v = 100 + 110 * Math.pow(Math.sin(t * Math.PI), 2) + 4 * Math.sin(t * 21);
            else v = 120 + 14 * Math.sin(t * 5) + 3 * Math.sin(t * 31);
            pts.push(v);
        }
        const line = pts
            .map((v, i) => {
                const x = (i / (N - 1)) * W;
                const y = H - ((v - 40) / 220) * H;
                return (i === 0 ? "M" : "L") + x.toFixed(2) + " " + y.toFixed(2);
            })
            .join(" ");
        const fill = `${line} L ${W} ${H} L 0 ${H} Z`;
        return { line, fill };
    }

    const chartPaths = charts.map((c) => buildPath(c.kind));

    // threshold y positions
    const yHigh = H - ((180 - 40) / 220) * H;
    const yLow = H - ((70 - 40) / 220) * H;

    type Chip = (typeof chips)[number];
    function chipStyle(c: Chip): string {
        const parts: string[] = [`top:${c.top}`, `transform:rotate(${c.rot}deg)`];
        if ("left" in c) parts.push(`left:${c.left}`);
        if ("right" in c) parts.push(`right:${c.right}`);
        return parts.join(";");
    }

    type Chart = (typeof charts)[number];
    function chartStyle(c: Chart): string {
        const parts: string[] = [`top:${c.top}`, `width:${c.width}px`, `transform:rotate(${c.rot}deg)`];
        if ("left" in c) parts.push(`left:${c.left}`);
        if ("right" in c) parts.push(`right:${c.right}`);
        return parts.join(";");
    }
</script>

<div class="aurora-pool" aria-hidden="true">
    {#each charts as chart, i}
        {@const path = chartPaths[i]}
        <div class="aurora-poolchart" style={chartStyle(chart)}>
            <div class="hd">
                <span>{chart.label}</span>
                <span class="dot"></span>
            </div>
            <svg viewBox="0 0 {W} {H}" preserveAspectRatio="none">
                <line x1="0" x2={W} y1={yHigh} y2={yHigh} class="thr" />
                <line x1="0" x2={W} y1={yLow} y2={yLow} class="thr" />
                <path d={path.fill} class="fill" />
                <path d={path.line} class="line" />
            </svg>
            <div class="ax"><span>40</span><span>260 mg/dL</span></div>
        </div>
    {/each}

    <div class="aurora-tirbar" style="top:38%;left:42%;transform:rotate(-4deg)">
        <div class="hd"><span>14-DAY DISTRIBUTION</span><span class="dot"></span></div>
        <div class="bar">
            {#each [{ v: 1, c: "var(--glucose-very-low)" }, { v: 4, c: "var(--glucose-low)" }, { v: 30, c: "var(--glucose-tight-range)" }, { v: 40, c: "var(--glucose-in-range)" }, { v: 25, c: "var(--glucose-high)" }] as b}
                <div style="flex:{b.v};background:{b.c}"></div>
            {/each}
        </div>
        <div class="rows">
            {#each [{ k: "Very Low", v: 1, c: "var(--glucose-very-low)" }, { k: "Low", v: 4, c: "var(--glucose-low)" }, { k: "TITR", v: 30, c: "var(--glucose-tight-range)" }, { k: "TIR", v: 40, c: "var(--glucose-in-range)" }, { k: "High", v: 25, c: "var(--glucose-high)" }] as b}
                <div class="row">
                    <span class="sw" style="background:{b.c}"></span>
                    <span class="k">{b.k}</span>
                    <span class="vv">{b.v}%</span>
                </div>
            {/each}
        </div>
    </div>

    {#each chips as chip}
        <div class="aurora-chip" style={chipStyle(chip)}>
            <img src="/logos/{chip.file}" alt="" />
            <span>{chip.name}</span>
        </div>
    {/each}
</div>

<style>
    .aurora-pool {
        position: absolute;
        inset: 0;
        pointer-events: none;
        overflow: hidden;
    }

    /* ── Mini chart card ── */
    .aurora-poolchart {
        position: absolute;
        background: oklch(0.13 0.028 261 / 72%);
        border: 1px solid oklch(1 0 0 / 12%);
        border-radius: 10px;
        padding: 8px 10px 6px;
        backdrop-filter: blur(8px);
        -webkit-backdrop-filter: blur(8px);
        filter: url(#aurora-water);
        mix-blend-mode: screen;
    }

    .aurora-poolchart .hd {
        display: flex;
        align-items: center;
        justify-content: space-between;
        font-family: ui-monospace, "SF Mono", Menlo, monospace;
        font-size: 9px;
        letter-spacing: 0.1em;
        color: oklch(0.85 0.01 261);
        margin-bottom: 5px;
    }

    .aurora-poolchart .hd .dot {
        width: 5px;
        height: 5px;
        border-radius: 999px;
        background: var(--glucose-in-range);
        opacity: 0.7;
    }

    .aurora-poolchart svg {
        width: 100%;
        height: 38px;
        display: block;
        overflow: visible;
    }

    .aurora-poolchart .thr {
        stroke: oklch(1 0 0 / 15%);
        stroke-width: 0.5;
        stroke-dasharray: 2 2;
    }

    .aurora-poolchart .fill {
        fill: var(--glucose-in-range);
        opacity: 0.12;
    }

    .aurora-poolchart .line {
        fill: none;
        stroke: var(--glucose-in-range);
        stroke-width: 1.5;
        stroke-linecap: round;
        stroke-linejoin: round;
        opacity: 0.85;
    }

    .aurora-poolchart .ax {
        display: flex;
        justify-content: space-between;
        font-family: ui-monospace, "SF Mono", Menlo, monospace;
        font-size: 8px;
        color: oklch(0.65 0.01 261);
        margin-top: 3px;
        font-variant-numeric: tabular-nums;
    }

    /* ── TIR bar ── */
    .aurora-tirbar {
        position: absolute;
        width: 180px;
        background: oklch(0.13 0.028 261 / 72%);
        border: 1px solid oklch(1 0 0 / 12%);
        border-radius: 10px;
        padding: 8px 10px;
        backdrop-filter: blur(8px);
        -webkit-backdrop-filter: blur(8px);
        filter: url(#aurora-water);
        mix-blend-mode: screen;
    }

    .aurora-tirbar .hd {
        display: flex;
        align-items: center;
        justify-content: space-between;
        font-family: ui-monospace, "SF Mono", Menlo, monospace;
        font-size: 9px;
        letter-spacing: 0.1em;
        color: oklch(0.85 0.01 261);
        margin-bottom: 6px;
    }

    .aurora-tirbar .hd .dot {
        width: 5px;
        height: 5px;
        border-radius: 999px;
        background: var(--glucose-in-range);
        opacity: 0.7;
    }

    .aurora-tirbar .bar {
        display: flex;
        height: 6px;
        border-radius: 4px;
        overflow: hidden;
        gap: 1px;
        margin-bottom: 8px;
    }

    .aurora-tirbar .rows {
        display: flex;
        flex-direction: column;
        gap: 3px;
    }

    .aurora-tirbar .row {
        display: flex;
        align-items: center;
        gap: 5px;
        font-size: 9px;
        font-family: ui-monospace, "SF Mono", Menlo, monospace;
    }

    .aurora-tirbar .sw {
        width: 8px;
        height: 8px;
        border-radius: 2px;
        flex-shrink: 0;
    }

    .aurora-tirbar .k {
        flex: 1;
        color: oklch(0.75 0.01 261);
    }

    .aurora-tirbar .vv {
        color: oklch(0.85 0.01 261);
        font-variant-numeric: tabular-nums;
    }

    /* ── Connector chip ── */
    .aurora-chip {
        position: absolute;
        display: flex;
        align-items: center;
        gap: 7px;
        background: oklch(0.13 0.028 261 / 60%);
        border: 1px solid oklch(1 0 0 / 15%);
        border-radius: 999px;
        padding: 5px 12px 5px 6px;
        backdrop-filter: blur(6px);
        -webkit-backdrop-filter: blur(6px);
        mix-blend-mode: screen;
    }

    .aurora-chip img {
        width: 20px;
        height: 20px;
        border-radius: 4px;
        object-fit: cover;
    }

    .aurora-chip span {
        font-size: 11px;
        font-weight: 500;
        color: oklch(0.9 0.01 261);
        white-space: nowrap;
    }
</style>
