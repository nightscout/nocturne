<script lang="ts">
    import { ArrowRight, Play, Check } from "@lucide/svelte";
    import { Button } from "@nocturne/ui/ui/button";
    import ReportsDemo from "$lib/components/features/ReportsDemo.svelte";
    import ConnectorsDemo from "$lib/components/features/ConnectorsDemo.svelte";
    import AlarmsDemo from "$lib/components/features/AlarmsDemo.svelte";
    import AuthDemo from "$lib/components/features/AuthDemo.svelte";

    const PILLARS = [
        {
            n: 1,
            eyebrow: "Reports",
            title: "Every report you actually use.",
            accent: "Built in.",
            body: "Time in Range. AGP. Day calendars. Meal impact. Pump activity. Sensor lifecycle. GLP-1 tracking. A dozen reports your endo asks for — already on the dashboard, ready to print or share.",
            bullets: ["12+ built-in reports", "Print, share, or schedule by email", "Drill from any chart into the raw data"],
            color: "oklch(0.6 0.118 184.704)",
        },
        {
            n: 2,
            eyebrow: "Connectors",
            title: "Plays nice with your gear.",
            accent: "Right out of the box.",
            body: "22 devices and services already wired in. Dexcom, Libre, Omnipod, Tandem, Loop, Trio, xDrip, Nightscout, Home Assistant. If your stuff is on the list, it works on day one.",
            bullets: ["Just sign in to your CGM — no fiddling", "Two-way bridge with Nightscout", "New connector every release on average"],
            color: "oklch(0.72 0.16 150)",
        },
        {
            n: 3,
            eyebrow: "Alarms",
            title: "Tell Nocturne what to do.",
            accent: "It will.",
            body: "Build alarms that actually fit your life. \"When my BG dips below 70, call my wife and flash the bedroom lights.\" Drag-and-drop rules, no scripts required.",
            bullets: ["When-this-then-that recipes", "Send to phone, watch, Discord, Slack, Home Assistant", "Snooze, escalate, or skip when asleep"],
            color: "oklch(0.646 0.222 41.116)",
        },
        {
            n: 4,
            eyebrow: "Sign in",
            title: "No password to lose.",
            accent: "Or to leak.",
            body: "Sign in with a passkey on your phone, or with Google, Apple, or GitHub. Your health data stays on your server — Nocturne never sees it, never touches it.",
            bullets: ["Passkeys on every modern device", "Google · Apple · GitHub · Microsoft", "Self-hosted: nothing leaves your machine"],
            color: "oklch(0.65 0.18 270)",
        },
    ] as const;

    const SUPPORTING = [
        {
            title: "Your server, your data",
            copy: "Self-hosted on Docker, Aspire, or bare metal. No cloud middleman, no third-party tracker. PHI never leaves your infrastructure.",
            icon: "shield",
            color: "oklch(0.577 0.245 27.325)",
        },
        {
            title: "One install, many people",
            copy: "Run a household, a clinic, or a community on a single deployment. Each tenant fully isolated via row-level security.",
            icon: "users",
            color: "oklch(0.65 0.18 270)",
        },
        {
            title: "Sub-second updates",
            copy: "New readings arrive on every device the moment they're written. WebSocket-first — no five-minute polling delay.",
            icon: "zap",
            color: "oklch(0.769 0.188 70)",
        },
        {
            title: "Drop-in Nightscout",
            copy: "Full v1/v2/v3 API parity. xDrip, Loop, AAPS, watch faces — everything just keeps working after you migrate.",
            icon: "plug",
            color: "oklch(0.72 0.16 150)",
        },
        {
            title: "Built for years of data",
            copy: "Modern stack handles years of readings without slowdowns. Search a date from years ago and get an answer in milliseconds.",
            icon: "chart",
            color: "oklch(0.6 0.118 184.704)",
        },
        {
            title: "Always free, always open",
            copy: "AGPL-3.0 licensed. Auditable, forkable, never going behind a paywall. The community built it; the community owns it.",
            icon: "sparkle",
            color: "oklch(0.488 0.243 264.376)",
        },
    ] as const;

    // Inline SVG paths for supporting cards
    const ICON_PATHS: Record<string, string> = {
        shield:  "M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z",
        users:   "M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2M12 7a4 4 0 1 0 0-8 4 4 0 0 0 0 8zM22 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75",
        zap:     "M4 14a1 1 0 0 1-.78-1.63l9.9-10.2a.5.5 0 0 1 .86.46l-1.92 6.02A1 1 0 0 0 13 10h7a1 1 0 0 1 .78 1.63l-9.9 10.2a.5.5 0 0 1-.86-.46l1.92-6.02A1 1 0 0 0 11 14z",
        plug:    "M12 22v-5M9 8V2M15 8V2M18 8v4a4 4 0 0 1-4 4h-4a4 4 0 0 1-4-4V8z",
        chart:   "M3 3v18h18M7 15l3-3 4 4 6-6",
        sparkle: "M9.937 15.5A2 2 0 0 0 8.5 14.063l-6.135-1.582a.5.5 0 0 1 0-.962L8.5 9.936A2 2 0 0 0 9.937 8.5l1.582-6.135a.5.5 0 0 1 .962 0L14.063 8.5A2 2 0 0 0 15.5 9.937l6.135 1.581a.5.5 0 0 1 0 .964L15.5 14.063a2 2 0 0 0-1.437 1.437l-1.582 6.135a.5.5 0 0 1-.962 0z",
    };
</script>

<div class="max-w-[1200px] mx-auto px-6">

    <!-- Hero -->
    <div class="pt-20 pb-16 border-b border-border relative overflow-hidden">
        <div class="aurora-subtle absolute inset-0 pointer-events-none" aria-hidden="true"></div>
        <div class="relative flex flex-col gap-6 max-w-[900px]">
            <div class="flex items-center gap-3 text-[17px] font-semibold uppercase tracking-[0.02em] text-glucose-in-range">
                <span class="size-2.5 rounded-full bg-glucose-in-range eyebrow-dot"></span>
                Everything Nocturne does
            </div>
            <h1 class="text-[clamp(2.4rem,5.5vw,4.5rem)] font-bold leading-[1.04] tracking-[-0.025em] text-foreground m-0">
                Big features.<br/>
                <em class="not-italic text-glucose-in-range font-semibold">Plain English.</em>
            </h1>
            <p class="text-[1.1rem] leading-[1.6] text-muted-foreground m-0 max-w-[680px]">
                Nocturne is a free, open-source diabetes dashboard you run on your own
                server. It connects to your CGM, pump, and apps; shows your numbers
                the way you want; and tells you the moment something needs attention.
            </p>
            <div class="flex flex-wrap gap-3 mt-2">
                <Button href="/docs/installation" size="lg" class="gap-2 text-base">
                    Get started <ArrowRight class="size-4" />
                </Button>
                <Button href="/demo" variant="outline" size="lg" class="gap-2 text-base">
                    <Play class="size-4" /> See a real day
                </Button>
            </div>
        </div>
    </div>

    <!-- THE FOUR KILLER FEATURES — alternating pillar layout -->
    {#each PILLARS as p, i (p.n)}
        {@const flip = i % 2 === 1}
        <section class="py-20 border-t border-border grid gap-16 items-center
                        {flip ? 'md:grid-cols-[1.05fr_1fr]' : 'md:grid-cols-[1fr_1.05fr]'}">

            <!-- Text -->
            <div class="flex flex-col gap-5 {flip ? 'md:order-2' : 'md:order-1'}">
                <!-- Eyebrow -->
                <div class="flex items-center gap-3 text-[17px] font-semibold tracking-[0.02em] uppercase"
                     style:color={p.color}>
                    <span class="font-mono text-[14px] px-2.5 py-0.5 rounded-full border border-current leading-6">
                        {String(p.n).padStart(2, '0')}
                    </span>
                    <span class="size-2.5 rounded-full shrink-0 eyebrow-dot" style:background={p.color} style:--dot-color={p.color}></span>
                    <span>{p.eyebrow}</span>
                </div>

                <!-- Heading -->
                <h2 class="text-[clamp(2rem,3.8vw,3.25rem)] font-bold leading-[1.06] tracking-[-0.025em] text-foreground m-0">
                    {p.title}<br/>
                    <em class="not-italic font-semibold" style:color={p.color}>{p.accent}</em>
                </h2>

                <!-- Body -->
                <p class="text-[1.0625rem] leading-[1.6] text-muted-foreground m-0 max-w-[520px]">{p.body}</p>

                <!-- Bullets -->
                <ul class="m-0 mt-2 p-0 list-none flex flex-col gap-3.5">
                    {#each p.bullets as b (b)}
                        <li class="flex items-start gap-3.5 text-[1rem] text-foreground/85">
                            <span class="shrink-0 size-[26px] rounded-full border-[1.5px] grid place-items-center mt-0.5"
                                  style:border-color={p.color}>
                                <Check class="size-3.5" style="color: {p.color}" />
                            </span>
                            {b}
                        </li>
                    {/each}
                </ul>
            </div>

            <!-- Demo -->
            <div class="relative {flip ? 'md:order-1' : 'md:order-2'}">
                <div class="absolute -inset-10 rounded-3xl pointer-events-none"
                     style="background: radial-gradient(60% 60% at 50% 50%, color-mix(in oklch, {p.color}, transparent 80%), transparent 70%); filter: blur(24px);"
                     aria-hidden="true"></div>
                <div class="relative">
                    {#if p.n === 1}
                        <ReportsDemo height={440} />
                    {:else if p.n === 2}
                        <ConnectorsDemo height={440} />
                    {:else if p.n === 3}
                        <AlarmsDemo height={560} />
                    {:else}
                        <AuthDemo height={440} />
                    {/if}
                </div>
            </div>
        </section>
    {/each}

    <!-- SUPPORTING — security, multitenant, developer -->
    <section class="py-20 border-t border-border">
        <div class="flex items-center gap-3 text-[17px] font-semibold tracking-[0.02em] uppercase text-muted-foreground mb-4">
            <span class="size-2.5 rounded-full bg-muted-foreground/40 shrink-0"></span>
            Also inside
        </div>
        <h2 class="text-[clamp(1.6rem,3.2vw,2.5rem)] font-bold leading-[1.1] tracking-[-0.025em] text-foreground m-0 mb-10">
            The unglamorous stuff <em class="text-glucose-in-range">still matters.</em>
        </h2>

        <div class="grid sm:grid-cols-2 lg:grid-cols-3 gap-4">
            {#each SUPPORTING as card (card.title)}
                <div class="p-7 rounded-2xl border border-border/60 bg-card/50 backdrop-blur-sm flex flex-col gap-3.5">
                    <div class="size-[52px] rounded-xl grid place-items-center"
                         style:background="color-mix(in oklch, {card.color}, transparent 85%)"
                         style:border="1px solid color-mix(in oklch, {card.color}, transparent 60%)">
                        <svg width="26" height="26" viewBox="0 0 24 24" fill="none"
                             stroke={card.color} stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"
                             aria-hidden="true">
                            <path d={ICON_PATHS[card.icon]}/>
                        </svg>
                    </div>
                    <h3 class="text-[1.15rem] font-bold text-foreground m-0 leading-[1.25]">{card.title}</h3>
                    <p class="text-[0.9375rem] leading-[1.6] text-muted-foreground m-0">{card.copy}</p>
                </div>
            {/each}
        </div>
    </section>

    <!-- COMPARE — plain-English before/after -->
    <section class="py-20 border-t border-border">
        <div class="flex items-center gap-3 text-[17px] font-semibold tracking-[0.02em] uppercase text-glucose-in-range mb-4">
            <span class="size-2.5 rounded-full bg-glucose-in-range shrink-0"></span>
            What changes
        </div>
        <h2 class="text-[clamp(1.6rem,3.2vw,2.5rem)] font-bold leading-[1.1] tracking-[-0.025em] text-foreground m-0 mb-10">
            The same data. <em class="text-glucose-in-range">Better everything else.</em>
        </h2>

        <div class="rounded-2xl overflow-hidden border border-border grid md:grid-cols-2">
            <!-- Old -->
            <div class="p-8 md:p-10 bg-[oklch(0.10_0.025_261)] flex flex-col gap-5">
                <div class="font-mono text-[13px] tracking-[0.08em] uppercase text-muted-foreground font-bold">
                    The old way
                </div>
                <h3 class="text-[1.5rem] font-bold text-muted-foreground m-0">Nightscout, circa 2014</h3>
                {#each [
                    "A handful of reports, all built in 2014",
                    "Each device needs a separate uploader app",
                    "Alarms = an on/off threshold, nothing more",
                    "Username & password — for life",
                    "Slows down after a year of readings",
                ] as line (line)}
                    <div class="flex items-center gap-3 text-[1rem] text-muted-foreground/70">
                        <span class="size-5 rounded-full bg-white/[0.06] grid place-items-center shrink-0
                                     font-mono text-[13px] text-muted-foreground/50">—</span>
                        {line}
                    </div>
                {/each}
            </div>

            <!-- New -->
            <div class="p-8 md:p-10 bg-gradient-to-b from-[oklch(0.16_0.03_261)] to-background flex flex-col gap-5 border-t md:border-t-0 md:border-l border-border relative overflow-hidden">
                <div class="absolute -top-5 -right-5 size-40 rounded-full pointer-events-none"
                     style="background: radial-gradient(circle, oklch(0.6 0.118 184.704 / 18%), transparent 60%)"
                     aria-hidden="true"></div>
                <div class="font-mono text-[13px] tracking-[0.08em] uppercase text-glucose-in-range font-bold relative">
                    Nocturne
                </div>
                <h3 class="text-[1.5rem] font-bold text-foreground m-0 relative">The rewrite, 2026</h3>
                {#each [
                    "12+ reports your endo asks for, on the dashboard",
                    "22 connectors out of the box — one click each",
                    "If-this-then-that recipes, send anywhere",
                    "Passkeys & sign-in-with-Google, no passwords",
                    "Modern database — years of data, sub-second",
                ] as line (line)}
                    <div class="flex items-center gap-3 text-[1rem] text-foreground/90 font-medium relative">
                        <span class="size-5 rounded-full bg-glucose-in-range/[0.18] border border-glucose-in-range/40
                                     grid place-items-center shrink-0">
                            <Check class="size-3 text-glucose-tight-range" />
                        </span>
                        {line}
                    </div>
                {/each}
            </div>
        </div>
    </section>

    <!-- CTA -->
    <section class="border-t border-border py-20 relative overflow-hidden">
        <div class="aurora-subtle absolute inset-0 pointer-events-none" aria-hidden="true"></div>
        <div class="relative max-w-[680px]">
            <h2 class="text-[clamp(2rem,4vw,3.5rem)] font-bold leading-[1.06] tracking-[-0.025em] text-foreground m-0 mb-4">
                Ready to take a look?<br/>
                <em class="not-italic text-glucose-in-range font-semibold">Five minutes, tops.</em>
            </h2>
            <p class="text-[1.0625rem] leading-[1.6] text-muted-foreground m-0 mb-8">
                Run your own copy with Docker, or poke around the demo first.
                No credit card, no waitlist, never any of that.
            </p>
            <div class="flex flex-wrap gap-3">
                <Button href="/docs/installation" size="lg" class="gap-2 text-base">
                    Installation guide <ArrowRight class="size-4" />
                </Button>
                <Button href="/demo" variant="outline" size="lg" class="gap-2 text-base">
                    <Play class="size-4" /> See the demo
                </Button>
                <Button href="/docs" variant="ghost" size="lg" class="text-base">
                    Read the docs
                </Button>
            </div>
        </div>
    </section>
</div>

<style>
    .aurora-subtle {
        background:
            radial-gradient(50% 40% at 80% 10%, oklch(0.5 0.18 264 / 18%) 0%, transparent 70%),
            radial-gradient(40% 30% at 10% 100%, oklch(0.72 0.16 150 / 12%) 0%, transparent 70%);
    }

    @keyframes eyebrow-pulse {
        0%, 100% { box-shadow: 0 0 0 3px color-mix(in oklch, var(--dot-color, var(--glucose-in-range)), transparent 80%); }
        50%       { box-shadow: 0 0 0 7px color-mix(in oklch, var(--dot-color, var(--glucose-in-range)), transparent 92%); }
    }
    .eyebrow-dot {
        box-shadow: 0 0 0 3px color-mix(in oklch, var(--dot-color, var(--glucose-in-range)), transparent 80%);
        animation: eyebrow-pulse 2.4s ease-in-out infinite;
    }
</style>
