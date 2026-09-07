<script lang="ts">
    import { Check } from "@lucide/svelte";
    import { REPORTS } from "$lib/data/reports";

    // Palette shared by every preview: the app's glucose range colours plus a neutral ink ramp.
    const IN_RANGE = "oklch(0.6 0.118 184.704)";
    const LOW = "oklch(0.646 0.222 41.116)";
    const VERY_LOW = "oklch(0.577 0.245 27.325)";
    const HIGH = "oklch(0.65 0.18 270)";
    const VERY_HIGH = "oklch(0.55 0.25 15)";
    const INSULIN = "rgb(30,150,252)";
    const CARBS = "oklch(0.769 0.188 70)";
    const INK = "oklch(0.97 0.005 261)";
    const INK_SOFT = "oklch(0.65 0.02 261)";
    const INK_FAINT = "oklch(0.50 0.02 261)";
    const PANEL = "oklch(0.16 0.03 261)";
    const MONO = "ui-monospace,monospace";
    const SANS = "system-ui,sans-serif";

    // Deterministic shapes so the demo renders identically on every load.
    const WEEK_TIR: [string, number][] = [["M", 0.71], ["T", 0.58], ["W", 0.83], ["T", 0.66], ["F", 0.74], ["S", 0.52], ["S", 0.79]];
    const MONTHS: [string, number][] = [["Oct", 138], ["Nov", 146], ["Dec", 152], ["Jan", 141], ["Feb", 134], ["Mar", 129]];
    const STEPS: number[] = [6200, 9800, 4100, 11200, 8400, 12600, 7300];
    const DAY_TRACES = [
        "M0,22 C20,10 40,30 60,18 S100,26 130,14 S160,30 180,20",
        "M0,18 C25,28 45,8 70,16 S110,30 140,12 S165,22 180,16",
        "M0,26 C20,14 50,32 80,22 S120,8 150,24 S170,18 180,22",
        "M0,14 C30,24 55,12 85,20 S125,34 150,16 S170,10 180,18",
        "M0,20 C25,32 50,14 75,24 S115,12 145,26 S165,20 180,14",
    ];
    const WEEK_TRACES = [
        "M20,110 C70,60 120,140 170,90 S270,150 320,80 S380,120 380,100",
        "M20,120 C70,80 120,120 170,100 S270,130 320,100 S380,110 380,105",
        "M20,95 C70,55 120,130 170,85 S270,140 320,70 S380,115 380,90",
        "M20,130 C70,90 120,150 170,110 S270,160 320,95 S380,125 380,110",
        "M20,105 C70,65 120,125 170,95 S270,145 320,85 S380,118 380,98",
    ];

    interface Props { height?: number; }
    let { height = 340 }: Props = $props();

    let idx = $state(0);
    $effect(() => {
        const t = setInterval(() => { idx = (idx + 1) % REPORTS.length; }, 2400);
        return () => clearInterval(t);
    });
    let current = $derived(REPORTS[idx]);

    // Nineteen rows do not fit the demo's height, so the sidebar scrolls to keep
    // the highlighted report in view, the way the app's own sidebar would.
    let sidebarEl: HTMLDivElement | null = $state(null);
    $effect(() => {
        const list = sidebarEl;
        const row = list?.children[idx] as HTMLElement | undefined;
        if (!list || !row) return;
        const top = row.offsetTop - list.clientHeight / 2 + row.offsetHeight / 2;
        list.scrollTo({ top: Math.max(0, top), behavior: "smooth" });
    });
</script>

<div class="@container" style:height="{height}px">
<div
    class="h-full rounded-[14px] overflow-hidden border border-white/10 bg-[oklch(0.10_0.025_261)] grid
        grid-cols-[104px_minmax(0,1fr)] @sm:grid-cols-[140px_minmax(0,1fr)] @lg:grid-cols-[176px_minmax(0,1fr)]"
>
    <!-- Sidebar list: the app's report navigation -->
    <div
        bind:this={sidebarEl}
        class="relative bg-[oklch(0.15_0.03_261)] border-r border-white/[0.08] py-2 overflow-y-auto [scrollbar-width:none] [&::-webkit-scrollbar]:hidden flex flex-col"
    >
        {#each REPORTS as r, i (r.preview)}
            <div
                class="px-3 @sm:px-4 py-[3px] text-[11px] @sm:text-[12px] leading-[1.35] flex items-center justify-between gap-1 transition-all duration-300 border-l-[3px] shrink-0
                    {i === idx
                        ? 'text-foreground bg-glucose-in-range/[0.14] border-glucose-in-range font-semibold'
                        : 'text-[oklch(0.65_0.02_261)] border-transparent font-medium'}"
            >
                <span class="truncate">{r.short}</span>
                {#if i === idx}
                    <Check class="size-3 text-glucose-in-range shrink-0" />
                {/if}
            </div>
        {/each}
    </div>

    <!-- Preview pane -->
    <div class="p-3 @sm:p-[18px] flex flex-col gap-2.5 min-h-0 min-w-0">
        <div class="flex items-baseline justify-between gap-2 shrink-0">
            <div class="min-w-0">
                <div class="text-[17px] font-bold text-foreground truncate">{current.name}</div>
                <div class="font-mono text-[11px] text-muted-foreground mt-0.5">{current.group} · last 14 days</div>
            </div>
            <span class="font-mono text-[11px] text-muted-foreground shrink-0">
                {String(idx + 1).padStart(2, '0')} / {REPORTS.length}
            </span>
        </div>
        <div class="flex-1 rounded-lg overflow-hidden bg-[oklch(0.16_0.03_261)] min-h-0">
            <svg viewBox="0 0 400 200" class="w-full h-full" preserveAspectRatio="xMidYMid meet">
                <rect width="400" height="200" fill={PANEL}/>

                {#if current.preview === "summary"}
                    {#each [["Average", "142", "mg/dL"], ["GMI", "6.7", "%"], ["Time in range", "72", "%"], ["Variability", "33", "% CV"]] as [label, value, unit], i (label)}
                        <rect x={16 + i * 94} y="22" width="84" height="70" rx="6" fill="oklch(0.20 0.03 261)"/>
                        <text x={26 + i * 94} y="44" fill={INK_SOFT} font-size="9" font-family={MONO}>{label}</text>
                        <text x={26 + i * 94} y="74" fill={INK} font-size="22" font-weight="700" font-family={SANS}>{value}</text>
                        <text x={26 + i * 94} y="86" fill={INK_FAINT} font-size="8" font-family={MONO}>{unit}</text>
                    {/each}
                    <rect x="16" y="120" width="14" height="28" fill={VERY_LOW}/>
                    <rect x="30" y="120" width="26" height="28" fill={LOW}/>
                    <rect x="56" y="120" width="265" height="28" fill={IN_RANGE}/>
                    <rect x="321" y="120" width="48" height="28" fill={HIGH}/>
                    <rect x="369" y="120" width="15" height="28" fill={VERY_HIGH}/>
                    <text x="16" y="172" fill={INK_SOFT} font-size="10" font-family={MONO}>4% low · 72% in range · 24% high</text>

                {:else if current.preview === "agp"}
                    <rect x="0" y="68" width="400" height="64" fill="oklch(0.6 0.118 184.704 / 12%)"/>
                    <line x1="0" y1="68" x2="400" y2="68" stroke="oklch(0.6 0.118 184.704 / 50%)" stroke-dasharray="2 4"/>
                    <line x1="0" y1="132" x2="400" y2="132" stroke="oklch(0.6 0.118 184.704 / 50%)" stroke-dasharray="2 4"/>
                    <path d="M0,100 C50,40 100,140 150,90 S250,150 300,80 S400,120 400,100 L400,170 C350,180 300,140 250,160 S150,110 100,140 S50,170 0,160 Z" fill="oklch(0.38 0.12 264 / 35%)"/>
                    <path d="M0,100 C50,70 100,120 150,95 S250,120 300,90 S400,110 400,100 L400,150 C350,150 300,130 250,140 S150,120 100,130 S50,150 0,140 Z" fill="oklch(0.75 0.18 264 / 40%)"/>
                    <path d="M0,100 C50,80 100,110 150,95 S250,110 300,95 S400,105 400,100" stroke="oklch(0.85 0.10 264)" stroke-width="2" fill="none"/>
                    {#each [0, 1, 2, 3, 4, 5, 6] as i (i)}
                        <text x={i * 66 + 8} y="195" fill={INK_FAINT} font-size="9" font-family={MONO}>{i * 4}:00</text>
                    {/each}

                {:else if current.preview === "distribution"}
                    <rect x="20" y="68" width="14" height="64" fill={VERY_LOW}/>
                    <rect x="34" y="68" width="29" height="64" fill={LOW}/>
                    <rect x="63" y="68" width="259" height="64" fill={IN_RANGE}/>
                    <rect x="322" y="68" width="43" height="64" fill={HIGH}/>
                    <rect x="365" y="68" width="15" height="64" fill={VERY_HIGH}/>
                    <text x="20" y="50" fill={INK} font-size="26" font-weight="700" font-family={SANS}>72%</text>
                    <text x="82" y="50" fill={INK_SOFT} font-size="13" font-family={SANS}>in range</text>
                    <text x="20" y="155" fill={INK_FAINT} font-size="9" font-family={MONO}>VLO</text>
                    <text x="38" y="155" fill={INK_FAINT} font-size="9" font-family={MONO}>LOW</text>
                    <text x="90" y="155" fill={INK_FAINT} font-size="9" font-family={MONO}>IN RANGE 70-180</text>
                    <text x="305" y="155" fill={INK_FAINT} font-size="9" font-family={MONO}>HIGH</text>
                    <text x="360" y="155" fill={INK_FAINT} font-size="9" font-family={MONO}>VHI</text>

                {:else if current.preview === "quality"}
                    <text x="16" y="36" fill={INK} font-size="20" font-weight="700" font-family={SANS}>96%</text>
                    <text x="62" y="36" fill={INK_SOFT} font-size="12" font-family={SANS}>of expected readings received</text>
                    {#each Array.from({ length: 14 }, (_, d) => d) as d (d)}
                        <rect x="16" y={54 + d * 9} width="368" height="6" rx="1" fill={IN_RANGE} opacity="0.7"/>
                        {#if d === 3}
                            <rect x="212" y={54 + d * 9} width="38" height="6" rx="1" fill={VERY_LOW}/>
                        {:else if d === 9}
                            <rect x="96" y={54 + d * 9} width="22" height="6" rx="1" fill={VERY_LOW}/>
                        {/if}
                    {/each}
                    <text x="16" y="193" fill={INK_SOFT} font-size="10" font-family={MONO}>2 gaps · longest 2h 05m · sensor warm-up</text>

                {:else if current.preview === "year"}
                    {#each Array.from({ length: 7 }, (_, r) => r) as r (r)}
                        {#each Array.from({ length: 26 }, (_, c) => c) as c (`${r}-${c}`)}
                            {@const v = (Math.sin(c * 0.5 + r) + Math.cos(r * 0.7 - c * 0.2) + 2) / 4}
                            <rect x={40 + c * 13.4} y={46 + r * 18} width="11" height="16"
                                  fill={v > 0.62 ? HIGH : IN_RANGE} opacity={0.25 + v * 0.7} rx="1"/>
                        {/each}
                    {/each}
                    <text x="16" y="36" fill={INK} font-size="14" font-weight="700" font-family={SANS}>Every day since 2023</text>
                    {#each ["M", "T", "W", "T", "F", "S", "S"] as d, i (i)}
                        <text x="20" y={58 + i * 18} fill={INK_SOFT} font-size="10" font-family={MONO}>{d}</text>
                    {/each}
                    <text x="40" y="190" fill={INK_FAINT} font-size="9" font-family={MONO}>Jan</text>
                    <text x="200" y="190" fill={INK_FAINT} font-size="9" font-family={MONO}>Jul</text>
                    <text x="360" y="190" fill={INK_FAINT} font-size="9" font-family={MONO}>Dec</text>

                {:else if current.preview === "readings"}
                    {#each DAY_TRACES as d, i (i)}
                        <text x="16" y={38 + i * 34} fill={INK_SOFT} font-size="10" font-family={MONO}>Mar {12 - i}</text>
                        <rect x="70" y={22 + i * 34} width="230" height="28" rx="3" fill="oklch(0.20 0.03 261)"/>
                        <rect x="70" y={30 + i * 34} width="230" height="12" fill="oklch(0.6 0.118 184.704 / 14%)"/>
                        <g transform="translate(90, {24 + i * 34}) scale(1.05, 0.7)">
                            <path d={d} stroke={IN_RANGE} stroke-width="2.4" fill="none"/>
                        </g>
                        <text x="312" y={41 + i * 34} fill={INK} font-size="11" font-weight="600" font-family={SANS}>{[78, 64, 83, 71, 76][i]}%</text>
                        <text x="345" y={41 + i * 34} fill={INK_FAINT} font-size="9" font-family={MONO}>{[131, 148, 126, 140, 137][i]} avg</text>
                    {/each}

                {:else if current.preview === "day"}
                    {#each Array.from({ length: 24 }, (_, h) => h) as h (h)}
                        {@const intensity = Math.sin(h * 0.4) * 0.4 + 0.5}
                        <rect x={16 + h * 15} y={80 - intensity * 40} width="11" height={intensity * 80}
                              fill={IN_RANGE} opacity={0.5 + intensity * 0.5} rx="2"/>
                    {/each}
                    <text x="16" y="38" fill={INK} font-size="16" font-weight="700" font-family={SANS}>Tuesday, March 12</text>
                    <text x="16" y="56" fill={INK_SOFT} font-size="11" font-family={MONO}>122 avg · 78% in range · 6 treatments</text>
                    {#each [0, 6, 12, 18] as h (h)}
                        <text x={16 + h * 15} y="196" fill={INK_FAINT} font-size="9" font-family={MONO}>{String(h).padStart(2, '0')}h</text>
                    {/each}

                {:else if current.preview === "week"}
                    <rect x="20" y="78" width="360" height="50" fill="oklch(0.6 0.118 184.704 / 12%)"/>
                    {#each WEEK_TRACES as d, i (i)}
                        <path d={d} stroke={INK_SOFT} stroke-width="1.2" fill="none" opacity="0.45"/>
                    {/each}
                    <path d="M20,112 C70,72 120,134 170,96 S270,146 320,86 S380,118 380,100" stroke={IN_RANGE} stroke-width="2.6" fill="none"/>
                    <text x="16" y="32" fill={INK} font-size="14" font-weight="700" font-family={SANS}>Seven days, overlaid</text>
                    <text x="16" y="48" fill={INK_SOFT} font-size="10" font-family={MONO}>Sunday runs 22 mg/dL higher after lunch</text>
                    {#each [0, 6, 12, 18, 24] as h, i (h)}
                        <text x={20 + i * 88} y="190" fill={INK_FAINT} font-size="9" font-family={MONO}>{String(h).padStart(2, '0')}:00</text>
                    {/each}

                {:else if current.preview === "month"}
                    {#each MONTHS as [m, avg], i (m)}
                        {@const h = (avg - 100) * 1.6}
                        <rect x={28 + i * 60} y={150 - h} width="40" height={h} fill={avg > 145 ? HIGH : IN_RANGE} opacity="0.75" rx="3"/>
                        <text x={48 + i * 60} y="170" text-anchor="middle" fill={INK_SOFT} font-size="11" font-family={MONO}>{m}</text>
                        <text x={48 + i * 60} y="185" text-anchor="middle" fill={INK_FAINT} font-size="10" font-family={MONO}>{avg}</text>
                    {/each}
                    <text x="20" y="32" fill={INK} font-size="14" font-weight="700" font-family={SANS}>Average glucose by month</text>
                    <text x="20" y="48" fill={INK_SOFT} font-size="10" font-family={MONO}>down 23 mg/dL since December</text>

                {:else if current.preview === "comparison"}
                    <rect x="20" y="78" width="360" height="50" fill="oklch(0.6 0.118 184.704 / 12%)"/>
                    <path d="M20,128 C70,80 120,150 170,104 S270,158 320,96 S380,128 380,112" stroke={INK_SOFT} stroke-width="2" fill="none" stroke-dasharray="5 4"/>
                    <path d="M20,112 C70,72 120,134 170,96 S270,140 320,86 S380,118 380,100" stroke={IN_RANGE} stroke-width="2.6" fill="none"/>
                    <text x="16" y="32" fill={INK} font-size="14" font-weight="700" font-family={SANS}>Before vs after the basal change</text>
                    <line x1="16" y1="48" x2="36" y2="48" stroke={INK_SOFT} stroke-width="2" stroke-dasharray="5 4"/>
                    <text x="42" y="51" fill={INK_SOFT} font-size="10" font-family={MONO}>Feb 1-14 · 64% in range</text>
                    <line x1="200" y1="48" x2="220" y2="48" stroke={IN_RANGE} stroke-width="2.6"/>
                    <text x="226" y="51" fill={INK_SOFT} font-size="10" font-family={MONO}>Feb 15-28 · 76% in range</text>

                {:else if current.preview === "steps"}
                    {#each STEPS as n, i (i)}
                        {@const h = n / 100}
                        <rect x={28 + i * 52} y={160 - h} width="36" height={h} fill={n >= 8000 ? IN_RANGE : INK_FAINT} opacity="0.8" rx="3"/>
                        <text x={46 + i * 52} y="178" text-anchor="middle" fill={INK_SOFT} font-size="11" font-family={MONO}>{WEEK_TIR[i][0]}</text>
                    {/each}
                    <text x="20" y="32" fill={INK} font-size="20" font-weight="700" font-family={SANS}>8,514</text>
                    <text x="82" y="32" fill={INK_SOFT} font-size="12" font-family={SANS}>steps a day on average</text>
                    <line x1="20" y1="75" x2="380" y2="75" stroke={INK_SOFT} stroke-dasharray="3 4"/>
                    <text x="330" y="70" fill={INK_FAINT} font-size="9" font-family={MONO}>8,000 goal</text>

                {:else if current.preview === "heart"}
                    <path d="M20,120 C50,118 60,90 90,96 S130,60 160,74 S210,130 240,122 S290,80 320,92 S360,124 380,118" stroke={LOW} stroke-width="2.4" fill="none"/>
                    <path d="M20,120 C50,118 60,90 90,96 S130,60 160,74 S210,130 240,122 S290,80 320,92 S360,124 380,118 L380,165 L20,165 Z" fill="oklch(0.646 0.222 41.116 / 15%)"/>
                    <line x1="20" y1="140" x2="380" y2="140" stroke={INK_SOFT} stroke-dasharray="3 4"/>
                    <text x="20" y="34" fill={INK} font-size="20" font-weight="700" font-family={SANS}>58</text>
                    <text x="48" y="34" fill={INK_SOFT} font-size="12" font-family={SANS}>bpm resting estimate</text>
                    <text x="300" y="136" fill={INK_FAINT} font-size="9" font-family={MONO}>resting 58 bpm</text>
                    {#each [0, 6, 12, 18, 24] as h, i (h)}
                        <text x={20 + i * 88} y="190" fill={INK_FAINT} font-size="9" font-family={MONO}>{String(h).padStart(2, '0')}:00</text>
                    {/each}

                {:else if current.preview === "sleep"}
                    <rect x="60" y="56" width="220" height="110" fill="oklch(0.65 0.18 270 / 14%)" rx="4"/>
                    <rect x="20" y="96" width="360" height="40" fill="oklch(0.6 0.118 184.704 / 12%)"/>
                    <path d="M20,112 C60,100 90,124 130,116 S200,104 240,110 S300,126 340,108 S370,98 380,100" stroke={IN_RANGE} stroke-width="2.6" fill="none"/>
                    <text x="16" y="34" fill={INK} font-size="14" font-weight="700" font-family={SANS}>Asleep 23:10 to 06:45</text>
                    <text x="16" y="50" fill={INK_SOFT} font-size="10" font-family={MONO}>84% in range overnight · no lows</text>
                    <text x="62" y="180" fill={INK_FAINT} font-size="9" font-family={MONO}>23:00</text>
                    <text x="262" y="180" fill={INK_FAINT} font-size="9" font-family={MONO}>07:00</text>

                {:else if current.preview === "treatments"}
                    {#each [
                        { t: "08:14", e: "Bolus 4.5u", c: INSULIN },
                        { t: "08:16", e: "Carbs 52g · oatmeal", c: CARBS },
                        { t: "10:42", e: "Site change", c: IN_RANGE },
                        { t: "12:30", e: "Bolus 6.2u", c: INSULIN },
                        { t: "12:35", e: "Carbs 71g · lunch", c: CARBS },
                        { t: "14:18", e: "Note: walk 30 min", c: INK_SOFT },
                    ] as row, i (row.t)}
                        <rect x="14" y={30 + i * 26} width="6" height="16" fill={row.c}/>
                        <text x="30" y={42 + i * 26} fill={INK_SOFT} font-size="11" font-family={MONO}>{row.t}</text>
                        <text x="76" y={42 + i * 26} fill={INK} font-size="11" font-family={SANS}>{row.e}</text>
                    {/each}

                {:else if current.preview === "basal"}
                    <path d="M0,140 L60,140 L60,100 L120,100 L120,140 L180,140 L180,80 L240,80 L240,130 L300,130 L300,110 L360,110 L360,140 L400,140"
                          stroke={INSULIN} stroke-width="2" fill="none"/>
                    <path d="M0,140 L60,140 L60,100 L120,100 L120,140 L180,140 L180,80 L240,80 L240,130 L300,130 L300,110 L360,110 L360,140 L400,140 L400,170 L0,170 Z"
                          fill="rgba(100,180,255,0.18)"/>
                    <path d="M0,150 L60,150 L60,118 L120,118 L120,146 L180,146 L180,96 L240,96 L240,138 L300,138 L300,124 L360,124 L360,150 L400,150"
                          stroke={INK_SOFT} stroke-width="1.5" fill="none" stroke-dasharray="4 3"/>
                    <text x="16" y="24" fill={INK} font-size="13" font-weight="700" font-family={SANS}>Scheduled basal vs what was delivered</text>
                    <text x="16" y="40" fill={INK_SOFT} font-size="10" font-family={MONO}>03:00 to 06:00 ran 22% above schedule</text>

                {:else if current.preview === "insulin"}
                    <circle cx="110" cy="105" r="58" fill="none" stroke={INK_FAINT} stroke-width="22" opacity="0.5"/>
                    <circle cx="110" cy="105" r="58" fill="none" stroke={INSULIN} stroke-width="22"
                            stroke-dasharray="167.6 196.8" transform="rotate(-90 110 105)"/>
                    <text x="110" y="101" text-anchor="middle" fill={INK} font-size="16" font-weight="700" font-family={SANS}>38.2u</text>
                    <text x="110" y="116" text-anchor="middle" fill={INK_SOFT} font-size="9" font-family={MONO}>per day</text>
                    <rect x="200" y="60" width="12" height="12" fill={INSULIN} rx="2"/>
                    <text x="220" y="70" fill={INK} font-size="12" font-family={SANS}>Basal 46%</text>
                    <text x="220" y="84" fill={INK_SOFT} font-size="10" font-family={MONO}>17.6u</text>
                    <rect x="200" y="106" width="12" height="12" fill={INK_FAINT} rx="2" opacity="0.6"/>
                    <text x="220" y="116" fill={INK} font-size="12" font-family={SANS}>Bolus 54%</text>
                    <text x="220" y="130" fill={INK_SOFT} font-size="10" font-family={MONO}>20.6u · 5.2 boluses a day</text>

                {:else if current.preview === "site"}
                    {#each [[0, 74, 2.8], [1, 70, 3.1], [2, 61, 2.4], [3, 77, 3.0], [4, 68, 3.3]] as [i, tir, days] (i)}
                        <rect x="90" y={46 + i * 28} width={days * 60} height="16" rx="3" fill={tir >= 70 ? IN_RANGE : LOW} opacity="0.7"/>
                        <text x="20" y={58 + i * 28} fill={INK_SOFT} font-size="10" font-family={MONO}>Site #{i + 1}</text>
                        <text x={96 + days * 60} y={58 + i * 28} fill={INK} font-size="10" font-weight="600" font-family={SANS}>{tir}%</text>
                    {/each}
                    <text x="20" y="36" fill={INK} font-size="14" font-weight="700" font-family={SANS}>Time in range per site</text>
                    <text x="20" y="193" fill={INK_SOFT} font-size="10" font-family={MONO}>day 3 runs 9% lower than day 1</text>

                {:else if current.preview === "idp"}
                    <text x="16" y="34" fill={INK} font-size="14" font-weight="700" font-family={SANS}>Insulin Dosing Profile</text>
                    <text x="16" y="50" fill={INK_SOFT} font-size="10" font-family={MONO}>standardised summary for your care team</text>
                    {#each [
                        ["Total daily dose", "38.2 u"],
                        ["Basal", "17.6 u · 46%"],
                        ["Bolus", "20.6 u · 54%"],
                        ["Carbohydrates", "182 g / day"],
                        ["Average glucose", "142 mg/dL"],
                        ["Time in range", "72%"],
                    ] as [k, v], i (k)}
                        <line x1="16" y1={66 + i * 21} x2="384" y2={66 + i * 21} stroke="oklch(1 0 0 / 8%)"/>
                        <text x="16" y={81 + i * 21} fill={INK_SOFT} font-size="11" font-family={SANS}>{k}</text>
                        <text x="384" y={81 + i * 21} text-anchor="end" fill={INK} font-size="11" font-weight="600" font-family={MONO}>{v}</text>
                    {/each}

                {:else if current.preview === "battery"}
                    <path d="M20,60 L120,120 L124,60 L230,130 L234,60 L340,138 L344,60 L380,82" stroke={IN_RANGE} stroke-width="2.2" fill="none"/>
                    <path d="M20,60 L120,120 L124,60 L230,130 L234,60 L340,138 L344,60 L380,82 L380,160 L20,160 Z" fill="oklch(0.6 0.118 184.704 / 14%)"/>
                    <line x1="20" y1="140" x2="380" y2="140" stroke={LOW} stroke-dasharray="3 4"/>
                    <text x="16" y="34" fill={INK} font-size="14" font-weight="700" font-family={SANS}>Pump battery</text>
                    <text x="16" y="50" fill={INK_SOFT} font-size="10" font-family={MONO}>a charge lasts 6.4 days · last change 2 days ago</text>
                    <text x="300" y="136" fill={INK_FAINT} font-size="9" font-family={MONO}>20% low warning</text>
                    <text x="16" y="190" fill={INK_FAINT} font-size="9" font-family={MONO}>Feb 20</text>
                    <text x="350" y="190" fill={INK_FAINT} font-size="9" font-family={MONO}>Mar 12</text>
                {/if}
            </svg>
        </div>
    </div>
</div>
</div>
