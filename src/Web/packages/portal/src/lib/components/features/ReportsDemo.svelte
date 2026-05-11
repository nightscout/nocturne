<script lang="ts">
    import { Check } from "@lucide/svelte";

    const REPORTS = [
        { name: "Ambulatory Glucose Profile", short: "AGP",   kind: "agp" },
        { name: "Time in Range",              short: "TIR",   kind: "tir" },
        { name: "Glycemia Risk Index",        short: "GRI",   kind: "gri" },
        { name: "Day Calendar",               short: "Day",   kind: "calendar" },
        { name: "Hourly Patterns",            short: "Hourly",kind: "heatmap" },
        { name: "Meal Impact",                short: "Meals", kind: "meals" },
        { name: "Sensor Lifecycle",           short: "CAGE",  kind: "lifecycle" },
        { name: "Pump Activity",              short: "Pump",  kind: "pump" },
        { name: "Insulin on Board",           short: "IOB",   kind: "iob" },
        { name: "Weekly Summary",             short: "Week",  kind: "weekly" },
        { name: "Treatment Log",              short: "Log",   kind: "log" },
        { name: "GLP-1 Tracking",             short: "GLP-1", kind: "glp1" },
    ] as const;

    const WEEKLY_BARS: [string, number][] = [["M",0.71],["T",0.58],["W",0.83],["T",0.66],["F",0.74],["S",0.52],["S",0.79]];

    interface Props { height?: number; }
    let { height = 340 }: Props = $props();

    let idx = $state(0);
    $effect(() => {
        const t = setInterval(() => { idx = (idx + 1) % REPORTS.length; }, 2400);
        return () => clearInterval(t);
    });
    let current = $derived(REPORTS[idx]);
</script>

<div
    class="rounded-[14px] overflow-hidden border border-white/10 bg-[oklch(0.10_0.025_261)] grid"
    style:height="{height}px"
    style="grid-template-columns: 168px 1fr"
>
    <!-- Sidebar list -->
    <div class="bg-[oklch(0.15_0.03_261)] border-r border-white/[0.08] py-3 overflow-hidden flex flex-col">
        {#each REPORTS as r, i (r.kind)}
            <div
                class="px-4 py-2 text-[13px] flex items-center justify-between transition-all duration-300 border-l-[3px] shrink-0
                    {i === idx
                        ? 'text-foreground bg-glucose-in-range/[0.14] border-glucose-in-range font-semibold'
                        : 'text-[oklch(0.65_0.02_261)] border-transparent font-medium'}"
            >
                <span>{r.short}</span>
                {#if i === idx}
                    <Check class="size-3 text-glucose-in-range shrink-0" />
                {/if}
            </div>
        {/each}
    </div>

    <!-- Preview pane -->
    <div class="p-[18px] flex flex-col gap-2.5 min-h-0">
        <div class="flex items-baseline justify-between gap-2 shrink-0">
            <div class="min-w-0">
                <div class="text-[17px] font-bold text-foreground truncate">{current.name}</div>
                <div class="font-mono text-[11px] text-muted-foreground mt-0.5">report · last 14 days</div>
            </div>
            <span class="font-mono text-[11px] text-muted-foreground shrink-0">
                {String(idx + 1).padStart(2, '0')} / {REPORTS.length}
            </span>
        </div>
        <div class="flex-1 rounded-lg overflow-hidden bg-[oklch(0.16_0.03_261)] min-h-0">
            {#if current.kind === "agp"}
                <svg viewBox="0 0 400 200" class="w-full h-full" preserveAspectRatio="xMidYMid meet">
                    <rect width="400" height="200" fill="oklch(0.16 0.03 261)"/>
                    <rect x="0" y="68" width="400" height="64" fill="oklch(0.6 0.118 184.704 / 12%)"/>
                    <line x1="0" y1="68" x2="400" y2="68" stroke="oklch(0.6 0.118 184.704 / 50%)" stroke-dasharray="2 4"/>
                    <line x1="0" y1="132" x2="400" y2="132" stroke="oklch(0.6 0.118 184.704 / 50%)" stroke-dasharray="2 4"/>
                    <path d="M0,100 C50,40 100,140 150,90 S250,150 300,80 S400,120 400,100 L400,170 C350,180 300,140 250,160 S150,110 100,140 S50,170 0,160 Z" fill="oklch(0.38 0.12 264 / 35%)"/>
                    <path d="M0,100 C50,70 100,120 150,95 S250,120 300,90 S400,110 400,100 L400,150 C350,150 300,130 250,140 S150,120 100,130 S50,150 0,140 Z" fill="oklch(0.75 0.18 264 / 40%)"/>
                    <path d="M0,100 C50,80 100,110 150,95 S250,110 300,95 S400,105 400,100" stroke="oklch(0.85 0.10 264)" stroke-width="2" fill="none"/>
                    {#each [0,1,2,3,4,5,6] as i (i)}
                        <text x={i*66+8} y="195" fill="oklch(0.55 0.02 261)" font-size="9" font-family="ui-monospace,monospace">{i*4}:00</text>
                    {/each}
                </svg>
            {:else if current.kind === "tir"}
                <svg viewBox="0 0 400 200" class="w-full h-full" preserveAspectRatio="xMidYMid meet">
                    <rect width="400" height="200" fill="oklch(0.16 0.03 261)"/>
                    <rect x="20" y="68" width="14"  height="64" fill="oklch(0.577 0.245 27.325)"/>
                    <rect x="34" y="68" width="29"  height="64" fill="oklch(0.646 0.222 41.116)"/>
                    <rect x="63" y="68" width="259" height="64" fill="oklch(0.6 0.118 184.704)"/>
                    <rect x="322" y="68" width="43" height="64" fill="oklch(0.65 0.18 270)"/>
                    <rect x="365" y="68" width="15" height="64" fill="oklch(0.55 0.25 15)"/>
                    <text x="20" y="50" fill="oklch(0.97 0.005 261)" font-size="26" font-weight="700" font-family="system-ui,sans-serif">72%</text>
                    <text x="82" y="50" fill="oklch(0.65 0.02 261)" font-size="13" font-family="system-ui,sans-serif">in range</text>
                    <text x="20" y="155" fill="oklch(0.55 0.02 261)" font-size="9" font-family="ui-monospace,monospace">VLO</text>
                    <text x="38" y="155" fill="oklch(0.55 0.02 261)" font-size="9" font-family="ui-monospace,monospace">LOW</text>
                    <text x="90" y="155" fill="oklch(0.55 0.02 261)" font-size="9" font-family="ui-monospace,monospace">IN RANGE 70–180</text>
                    <text x="305" y="155" fill="oklch(0.55 0.02 261)" font-size="9" font-family="ui-monospace,monospace">HIGH</text>
                    <text x="360" y="155" fill="oklch(0.55 0.02 261)" font-size="9" font-family="ui-monospace,monospace">VHI</text>
                </svg>
            {:else if current.kind === "gri"}
                <svg viewBox="0 0 400 200" class="w-full h-full" preserveAspectRatio="xMidYMid meet">
                    <rect width="400" height="200" fill="oklch(0.16 0.03 261)"/>
                    <rect x="50"  y="150" width="56" height="20"  fill="oklch(0.72 0.17 145)"/>
                    <rect x="110" y="142" width="56" height="28"  fill="oklch(0.78 0.15 120)"/>
                    <rect x="170" y="134" width="56" height="36"  fill="oklch(0.80 0.16 90)"/>
                    <rect x="230" y="126" width="56" height="44"  fill="oklch(0.70 0.17 55)"/>
                    <rect x="290" y="118" width="56" height="52"  fill="oklch(0.60 0.20 25)"/>
                    <text x="50" y="40" fill="oklch(0.97 0.005 261)" font-size="24" font-weight="700" font-family="system-ui,sans-serif">22.4</text>
                    <text x="118" y="40" fill="oklch(0.65 0.02 261)" font-size="12">Zone B</text>
                    <text x="50" y="58" fill="oklch(0.65 0.02 261)" font-size="11" font-family="ui-monospace,monospace">GRI · last 90 days</text>
                    {#each ["A","B","C","D","E"] as l, i (l)}
                        <text x={62 + i*60} y="185" fill="oklch(0.55 0.02 261)" font-size="11" font-family="ui-monospace,monospace">{l}</text>
                    {/each}
                </svg>
            {:else if current.kind === "calendar"}
                <svg viewBox="0 0 400 200" class="w-full h-full" preserveAspectRatio="xMidYMid meet">
                    <rect width="400" height="200" fill="oklch(0.16 0.03 261)"/>
                    {#each Array.from({length: 24}, (_, h) => h) as h (h)}
                        {@const intensity = Math.sin(h * 0.4) * 0.4 + 0.5}
                        <rect x={16 + h * 15} y={80 - intensity*40} width="11" height={intensity*80}
                              fill="oklch(0.6 0.118 184.704)" opacity={0.5 + intensity*0.5} rx="2"/>
                    {/each}
                    <text x="16" y="38" fill="oklch(0.97 0.005 261)" font-size="16" font-weight="700">Tuesday, March 12</text>
                    <text x="16" y="56" fill="oklch(0.65 0.02 261)" font-size="11" font-family="ui-monospace,monospace">122 avg · 78% TIR · 6 entries</text>
                    {#each [0,6,12,18] as h (h)}
                        <text x={16 + h*15} y="196" fill="oklch(0.50 0.02 261)" font-size="9" font-family="ui-monospace,monospace">{String(h).padStart(2,'0')}h</text>
                    {/each}
                </svg>
            {:else if current.kind === "heatmap"}
                <svg viewBox="0 0 400 200" class="w-full h-full" preserveAspectRatio="xMidYMid meet">
                    <rect width="400" height="200" fill="oklch(0.16 0.03 261)"/>
                    {#each Array.from({length: 7}, (_, r) => r) as r (r)}
                        {#each Array.from({length: 24}, (_, c) => c) as c (`${r}-${c}`)}
                            {@const v = (Math.sin(c*0.5 + r) + Math.cos(r*0.7 - c*0.2) + 2) / 4}
                            <rect x={40 + c*14} y={46 + r*18} width="12" height="16"
                                  fill={v > 0.6 ? "oklch(0.65 0.18 270)" : "oklch(0.6 0.118 184.704)"}
                                  opacity={0.25 + v*0.7} rx="1"/>
                        {/each}
                    {/each}
                    <text x="16" y="36" fill="oklch(0.97 0.005 261)" font-size="14" font-weight="700">Hourly Patterns · 30 days</text>
                    {#each ["M","T","W","T","F","S","S"] as d, i (i)}
                        <text x="20" y={58 + i*18} fill="oklch(0.65 0.02 261)" font-size="10" font-family="ui-monospace,monospace">{d}</text>
                    {/each}
                </svg>
            {:else if current.kind === "meals"}
                <svg viewBox="0 0 400 200" class="w-full h-full" preserveAspectRatio="xMidYMid meet">
                    <rect width="400" height="200" fill="oklch(0.16 0.03 261)"/>
                    {#each [40,70,95,80,60,55,50,45,42] as y, i (i)}
                        <path d="M{30 + i*42},150 L{38 + i*42},{150 - y*0.8}" stroke="oklch(0.65 0.18 270)" stroke-width="2"/>
                        <circle cx={38 + i*42} cy={150 - y*0.8} r="3" fill="oklch(0.65 0.18 270)"/>
                    {/each}
                    <rect x="22" y="48" width="6" height="100" fill="oklch(0.769 0.188 70)" opacity="0.7"/>
                    <text x="38" y="45" fill="oklch(0.97 0.005 261)" font-size="13" font-weight="600">Pasta · 65g carbs</text>
                    <text x="38" y="60" fill="oklch(0.65 0.02 261)" font-size="11" font-family="ui-monospace,monospace">peak +98 · back in range 2h 40m</text>
                    {#each [0,1,2,3] as h (h)}
                        <text x={28 + h*84} y="185" fill="oklch(0.50 0.02 261)" font-size="10" font-family="ui-monospace,monospace">{h}h</text>
                    {/each}
                </svg>
            {:else if current.kind === "lifecycle"}
                <svg viewBox="0 0 400 200" class="w-full h-full" preserveAspectRatio="xMidYMid meet">
                    <rect width="400" height="200" fill="oklch(0.16 0.03 261)"/>
                    {#each [0,1,2,3,4] as i (i)}
                        <rect x="80" y={46 + i*28} width={180 + i*20} height="16" rx="3" fill="oklch(0.6 0.118 184.704)" opacity="0.65"/>
                        <text x="20" y={58 + i*28} fill="oklch(0.65 0.02 261)" font-size="10" font-family="ui-monospace,monospace">G7 · #{i+1}</text>
                    {/each}
                    <text x="20" y="36" fill="oklch(0.97 0.005 261)" font-size="14" font-weight="700">CAGE · last 5 sensors</text>
                    <text x="20" y="193" fill="oklch(0.65 0.02 261)" font-size="10" font-family="ui-monospace,monospace">avg wear 9.4d · longest 10.5d</text>
                </svg>
            {:else if current.kind === "pump"}
                <svg viewBox="0 0 400 200" class="w-full h-full" preserveAspectRatio="xMidYMid meet">
                    <rect width="400" height="200" fill="oklch(0.16 0.03 261)"/>
                    <path d="M0,140 L60,140 L60,100 L120,100 L120,140 L180,140 L180,80 L240,80 L240,130 L300,130 L300,110 L360,110 L360,140 L400,140"
                          stroke="rgb(30,150,252)" stroke-width="2" fill="none"/>
                    <path d="M0,140 L60,140 L60,100 L120,100 L120,140 L180,140 L180,80 L240,80 L240,130 L300,130 L300,110 L360,110 L360,140 L400,140 L400,170 L0,170 Z"
                          fill="rgba(100,180,255,0.18)"/>
                    {#each [[80,3.2],[160,1.8],[240,4.5],[320,2.1]] as [x, u] (`${x}`)}
                        <rect x={x-2} y="40" width="4" height="14" fill="rgb(30,150,252)"/>
                        <text x={x-6} y="32" fill="oklch(0.65 0.02 261)" font-size="9" font-family="ui-monospace,monospace">{u}u</text>
                    {/each}
                    <text x="16" y="22" fill="oklch(0.97 0.005 261)" font-size="13" font-weight="700">Basal + boluses · today</text>
                </svg>
            {:else if current.kind === "iob"}
                <svg viewBox="0 0 400 200" class="w-full h-full" preserveAspectRatio="xMidYMid meet">
                    <rect width="400" height="200" fill="oklch(0.16 0.03 261)"/>
                    <path d="M30,160 Q80,40 130,80 T260,120 T380,150" stroke="rgb(30,150,252)" stroke-width="3" fill="none"/>
                    <path d="M30,160 Q80,40 130,80 T260,120 T380,150 L380,170 L30,170 Z" fill="rgba(30,150,252,0.18)"/>
                    <text x="20" y="40" fill="oklch(0.97 0.005 261)" font-size="20" font-weight="700" font-family="system-ui,sans-serif">2.4u</text>
                    <text x="70" y="40" fill="oklch(0.65 0.02 261)" font-size="12">on board</text>
                    <text x="20" y="58" fill="oklch(0.65 0.02 261)" font-size="11" font-family="ui-monospace,monospace">decay over 4h · DIA 4.0</text>
                </svg>
            {:else if current.kind === "weekly"}
                <svg viewBox="0 0 400 200" class="w-full h-full" preserveAspectRatio="xMidYMid meet">
                    <rect width="400" height="200" fill="oklch(0.16 0.03 261)"/>
                    {#each WEEKLY_BARS as [d, tir], i (`${d}-${i}`)}
                        <rect x={28 + i*52} y={150 - tir*100} width="36" height={tir*100} fill="oklch(0.6 0.118 184.704)" opacity="0.75" rx="3"/>
                        <text x={46 + i*52} y="170" text-anchor="middle" fill="oklch(0.65 0.02 261)" font-size="11" font-family="ui-monospace,monospace">{d}</text>
                        <text x={46 + i*52} y="185" text-anchor="middle" fill="oklch(0.55 0.02 261)" font-size="10" font-family="ui-monospace,monospace">{Math.round(tir*100)}%</text>
                    {/each}
                    <text x="20" y="32" fill="oklch(0.97 0.005 261)" font-size="14" font-weight="700">Week of Mar 11</text>
                </svg>
            {:else if current.kind === "log"}
                <svg viewBox="0 0 400 200" class="w-full h-full" preserveAspectRatio="xMidYMid meet">
                    <rect width="400" height="200" fill="oklch(0.16 0.03 261)"/>
                    {#each [
                        { t: "08:14", e: "Bolus 4.5u",          c: "rgb(30,150,252)" },
                        { t: "08:16", e: "Carbs 52g · oatmeal",  c: "oklch(0.769 0.188 70)" },
                        { t: "10:42", e: "Sensor change",         c: "oklch(0.6 0.118 184.704)" },
                        { t: "12:30", e: "Bolus 6.2u",            c: "rgb(30,150,252)" },
                        { t: "12:35", e: "Carbs 71g · lunch",     c: "oklch(0.769 0.188 70)" },
                        { t: "14:18", e: "Note: walk 30 min",     c: "oklch(0.65 0.02 261)" },
                    ] as row, i (row.t)}
                        <rect x="14" y={30 + i*26} width="6" height="16" fill={row.c}/>
                        <text x="30" y={42 + i*26} fill="oklch(0.65 0.02 261)" font-size="11" font-family="ui-monospace,monospace">{row.t}</text>
                        <text x="76" y={42 + i*26} fill="oklch(0.97 0.005 261)" font-size="11" font-family="system-ui,sans-serif">{row.e}</text>
                    {/each}
                </svg>
            {:else if current.kind === "glp1"}
                <svg viewBox="0 0 400 200" class="w-full h-full" preserveAspectRatio="xMidYMid meet">
                    <rect width="400" height="200" fill="oklch(0.16 0.03 261)"/>
                    <path d="M30,140 C80,140 110,80 160,80 C210,80 220,40 290,40 C330,40 350,30 380,30"
                          stroke="oklch(0.65 0.18 270)" stroke-width="2" fill="none" stroke-dasharray="4 4"/>
                    {#each [0,1,2,3,4,5,6,7] as i (i)}
                        <rect x={40 + i*42} y={140 - i*4} width="28" height={i*4 + 14} fill="oklch(0.6 0.118 184.704)" opacity="0.7" rx="2"/>
                    {/each}
                    <text x="20" y="32" fill="oklch(0.97 0.005 261)" font-size="14" font-weight="700">Mounjaro · TIR over 8 weeks</text>
                    <text x="20" y="193" fill="oklch(0.65 0.02 261)" font-size="10" font-family="ui-monospace,monospace">started 2.5mg · titrated to 7.5mg · TIR +18%</text>
                </svg>
            {/if}
        </div>
    </div>
</div>
