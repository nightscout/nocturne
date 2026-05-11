<script lang="ts">
    import { ArrowRight, Check } from "@lucide/svelte";
    import ReportsDemo from "./ReportsDemo.svelte";
    import ConnectorsDemo from "./ConnectorsDemo.svelte";
    import AlarmsDemo from "./AlarmsDemo.svelte";
    import AuthDemo from "./AuthDemo.svelte";

    interface Props {
        /** Pass a button/link component for the CTAs, or use default anchors */
        demoHeight?: number;
    }
    let { demoHeight = 400 }: Props = $props();

    const PILLARS = [
        {
            n: 1,
            eyebrow: "Reports",
            title: "Every report you actually use.",
            accent: "Built in.",
            body: "Time in Range. AGP. Day calendars. Meal impact. Pump activity. GLP-1 tracking. A dozen reports your endo asks for — already on the dashboard.",
            bullets: ["12+ built-in reports", "Print, share, or schedule by email", "Drill from any chart into the raw data"],
            color: "oklch(0.6 0.118 184.704)",
        },
        {
            n: 2,
            eyebrow: "Connectors",
            title: "Plays nice with your gear.",
            accent: "Right out of the box.",
            body: "22 devices and services already wired in. Dexcom, Libre, Omnipod, Tandem, Loop, Trio, xDrip, Nightscout, Home Assistant. If your stuff is on the list, it's on Nocturne.",
            bullets: ["Just sign in to your CGM — no fiddling", "Two-way bridge with Nightscout", "New connector every release"],
            color: "oklch(0.72 0.16 150)",
        },
        {
            n: 3,
            eyebrow: "Alarms",
            title: "Tell Nocturne what to do.",
            accent: "It will.",
            body: "Build alarms that fit your life. \"When my BG dips below 70, call my wife and flash the bedroom lights.\" Drag-and-drop rules, no scripts required.",
            bullets: ["When-this-then-that recipes", "Send to phone, watch, Discord, Slack, Home Assistant", "Snooze, escalate, or skip when asleep"],
            color: "oklch(0.646 0.222 41.116)",
        },
        {
            n: 4,
            eyebrow: "Sign in",
            title: "No password to lose.",
            accent: "Or to leak.",
            body: "Sign in with a passkey on your phone, or with Google, Apple, or GitHub. Your health data stays on your server — Nocturne never sees it.",
            bullets: ["Passkeys on every modern device", "Google · Apple · GitHub · Microsoft", "Self-hosted: nothing leaves your machine"],
            color: "oklch(0.65 0.18 270)",
        },
    ] as const;
</script>

<section class="max-w-[1200px] mx-auto px-6 border-t border-border">
    <!-- Section header -->
    <div class="pt-20 pb-14">
        <div class="font-mono text-[11px] tracking-[0.14em] uppercase text-muted-foreground mb-4">What it can do</div>
        <h2 class="text-[clamp(1.6rem,3.5vw,2.5rem)] font-bold leading-[1.2] tracking-[-0.02em] text-foreground m-0">
            Four things Nocturne is <em class="text-glucose-in-range">really good at.</em>
        </h2>
    </div>

    <!-- Pillars -->
    {#each PILLARS as p, i (p.n)}
        {@const flip = i % 2 === 1}
        <div class="py-14 border-t border-border grid gap-12 items-center
                    {flip ? 'md:grid-cols-[1.05fr_1fr]' : 'md:grid-cols-[1fr_1.05fr]'}">

            <!-- Text side -->
            <div class="flex flex-col gap-5 {flip ? 'md:order-2' : 'md:order-1'}">
                <!-- BigEyebrow -->
                <div class="flex items-center gap-3 text-[17px] font-semibold tracking-[0.02em] uppercase"
                     style:color={p.color}>
                    <span class="font-mono text-[14px] px-2.5 py-0.5 rounded-full border border-current">
                        {String(p.n).padStart(2, '0')}
                    </span>
                    <span class="size-2.5 rounded-full shrink-0 eyebrow-dot" style:background={p.color} style:--dot-color={p.color}></span>
                    <span>{p.eyebrow}</span>
                </div>

                <!-- Title -->
                <h3 class="text-[clamp(1.8rem,3.2vw,2.8rem)] font-bold leading-[1.08] tracking-[-0.025em] text-foreground m-0">
                    {p.title}<br/>
                    <em class="not-italic font-semibold" style:color={p.color}>{p.accent}</em>
                </h3>

                <!-- Body -->
                <p class="text-[1.05rem] leading-[1.6] text-muted-foreground m-0 max-w-[520px]">{p.body}</p>

                <!-- Bullets -->
                <ul class="m-0 mt-1 p-0 list-none flex flex-col gap-3">
                    {#each p.bullets as b (b)}
                        <li class="flex items-start gap-3.5 text-[1rem] text-foreground/85">
                            <span class="shrink-0 size-6 rounded-full border grid place-items-center mt-0.5" style:border-color={p.color}>
                                <Check class="size-3.5" style="color: {p.color}" />
                            </span>
                            {b}
                        </li>
                    {/each}
                </ul>
            </div>

            <!-- Demo side -->
            <div class="relative {flip ? 'md:order-1' : 'md:order-2'}">
                <!-- Radial glow -->
                <div class="absolute -inset-10 rounded-3xl pointer-events-none"
                     style="background: radial-gradient(60% 60% at 50% 50%, color-mix(in oklch, {p.color}, transparent 82%), transparent 70%); filter: blur(20px);"
                     aria-hidden="true"></div>
                <!-- Demo -->
                <div class="relative">
                    {#if p.n === 1}
                        <ReportsDemo height={demoHeight} />
                    {:else if p.n === 2}
                        <ConnectorsDemo height={demoHeight} />
                    {:else if p.n === 3}
                        <AlarmsDemo height={Math.max(520, demoHeight)} />
                    {:else}
                        <AuthDemo height={demoHeight} />
                    {/if}
                </div>
            </div>
        </div>
    {/each}

    <!-- CTA footer band -->
    <div class="border-t border-border py-12 flex flex-col sm:flex-row sm:items-center sm:justify-between gap-6">
        <div>
            <div class="text-[1.2rem] font-bold text-foreground">See it all running together</div>
            <div class="text-[0.9375rem] text-muted-foreground mt-1">Open the demo, or run your own copy in 5 minutes.</div>
        </div>
        <div class="flex flex-wrap gap-3 shrink-0">
            <a
                href="/docs/installation"
                class="inline-flex items-center gap-2 px-5 py-3 rounded-lg text-[0.9375rem] font-semibold transition-colors
                       bg-foreground text-background hover:opacity-90"
            >
                Get started <ArrowRight class="size-4" />
            </a>
            <a
                href="/features"
                class="inline-flex items-center gap-2 px-5 py-3 rounded-lg text-[0.9375rem] font-medium border border-border text-foreground hover:bg-muted/50 transition-colors"
            >
                All features
            </a>
        </div>
    </div>
</section>

<style>
    @keyframes eyebrow-pulse {
        0%, 100% { box-shadow: 0 0 0 3px color-mix(in oklch, var(--dot-color), transparent 80%); }
        50%       { box-shadow: 0 0 0 7px color-mix(in oklch, var(--dot-color), transparent 92%); }
    }
    .eyebrow-dot {
        animation: eyebrow-pulse 2.4s ease-in-out infinite;
        box-shadow: 0 0 0 3px color-mix(in oklch, var(--dot-color), transparent 80%);
    }
</style>
