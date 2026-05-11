<script lang="ts">
    import { Button } from "@nocturne/ui/ui/button";
    import { ArrowRight, Play } from "@lucide/svelte";
    import { DEMO_ENABLED } from "$lib/config";
    import AuroraCanvas from "$lib/components/AuroraCanvas.svelte";
    import AuroraPool from "$lib/components/AuroraPool.svelte";
    import { getCommunityData } from "$lib/data/portal.remote";

    let communityData = $state<Awaited<ReturnType<typeof getCommunityData>> | null>(null);
    getCommunityData({})
        .then((d) => (communityData = d))
        .catch(() => {});

    const connectors = [
        { file: "dexcom.png", name: "Dexcom" },
        { file: "libre.png", name: "FreeStyle Libre" },
        { file: "glooko.png", name: "Glooko" },
        { file: "medtronic.jpg", name: "Medtronic" },
        { file: "tandem.png", name: "Tandem" },
        { file: "omnipod.png", name: "Omnipod" },
        { file: "loop.png", name: "Loop" },
        { file: "trio.jpg", name: "Trio" },
        { file: "aaps.png", name: "AAPS" },
        { file: "xdrip.jpg", name: "xDrip+" },
        { file: "mylife.png", name: "myLife" },
        { file: "myfitnesspal.jpg", name: "MyFitnessPal" },
        { file: "tidepool.jpg", name: "Tidepool" },
        { file: "sugarmate.png", name: "Sugarmate" },
        { file: "spike.png", name: "Spike" },
        { file: "juggluco.png", name: "Juggluco" },
        { file: "glucotracker.png", name: "GlucoTracker" },
        { file: "nightscout.png", name: "Nightscout" },
        { file: "discord.png", name: "Discord" },
        { file: "slack.png", name: "Slack" },
        { file: "telegram.png", name: "Telegram" },
        { file: "home-assistant.png", name: "Home Assistant" },
    ];
</script>

<!-- Hero -->
<section class="aurora-hero-wrap">
    <svg class="aurora-water-defs" aria-hidden="true">
        <defs>
            <filter id="aurora-water" x="-10%" y="-10%" width="120%" height="120%">
                <feTurbulence type="fractalNoise" baseFrequency="0.012 0.018"
                    numOctaves="2" seed="3" result="t">
                    <animate attributeName="baseFrequency" dur="22s"
                        repeatCount="indefinite"
                        values="0.010 0.016; 0.018 0.022; 0.010 0.016" />
                </feTurbulence>
                <feDisplacementMap in="SourceGraphic" in2="t" scale="6"
                    xChannelSelector="R" yChannelSelector="G" />
            </filter>
        </defs>
    </svg>

    <AuroraCanvas height={920} />
    <div class="aurora-grain" aria-hidden="true"></div>
    <AuroraPool />
    <div class="aurora-fade" aria-hidden="true"></div>

    <div class="aurora-chrome">
        <div class="aurora-stamp aurora-stamp-tl">
            <span>NCTRN / HOMEPAGE</span>
            <span>v0.4 &middot; public preview</span>
        </div>
        <div class="aurora-stamp aurora-stamp-tr">
            <span>5,250 d &middot; running</span>
            <span>22 connectors</span>
        </div>

        <div class="aurora-hero-text">
            <span class="aurora-eyebrow">
                <span class="aurora-eyebrow-dot"></span>The new Nightscout API
            </span>
            <h1 class="aurora-h1">
                <span>Every reading.</span>
                <span><em>Every</em> source.</span>
                <span>One dashboard.</span>
            </h1>
            <p class="aurora-lede">
                Nocturne pulls every CGM, pump, and tracker you use into a single
                self-hosted dashboard. Fast, multitenant, real-time, and built by
                the diabetes community.
            </p>
            <div class="aurora-ctas">
                <Button href="/docs/installation" size="lg" class="gap-2 text-base aurora-btn-primary">
                    Get started <ArrowRight class="w-4 h-4" />
                </Button>
                {#if DEMO_ENABLED}
                    <Button href="/demo" variant="ghost" size="lg" class="gap-2 text-base aurora-btn-ghost">
                        <Play class="w-4 h-4" /> See a real day
                    </Button>
                {:else}
                    <Button href="/features" variant="ghost" size="lg" class="text-base aurora-btn-ghost">
                        Explore features
                    </Button>
                {/if}
            </div>
        </div>
    </div>
</section>

<!-- 01 Manifesto -->
<section class="aurora-section aurora-manifesto">
    <div class="aurora-section-head">
        <div class="aurora-section-label">&#167; 01 &mdash; Why this exists</div>
        <h2>
            The diabetes data stack has not moved in a decade.<br />
            <em>This is the rewrite.</em>
        </h2>
    </div>

    <div class="aurora-cols">
        <div class="aurora-col">
            <div class="aurora-col-num">01</div>
            <h3>Drop-in Nightscout API</h3>
            <p>
                Full v1/v2/v3 compatibility. Every existing app, watch face, and
                follower keeps working &mdash; migration is a connection-string change.
            </p>
        </div>
        <div class="aurora-col">
            <div class="aurora-col-num">02</div>
            <h3>Multitenant, by default</h3>
            <p>
                One install, many people. Run a household, a clinic, or a community
                on a single deployment with isolated data and per-tenant settings.
            </p>
        </div>
        <div class="aurora-col">
            <div class="aurora-col-num">03</div>
            <h3>Real-time, sub-second</h3>
            <p>
                WebSocket-first. New CGM readings arrive on every device the moment
                they are written &mdash; no five-minute polling delay.
            </p>
        </div>
    </div>
</section>

<!-- 02 Connectors -->
<section class="aurora-section aurora-connectors">
    <div class="aurora-section-head">
        <div class="aurora-section-label">&#167; 02 &mdash; What plugs in</div>
        <h2>22 sources. <em>One API surface.</em></h2>
    </div>

    <div class="aurora-conn-strip">
        <AuroraCanvas height={6} intensity={1.2} speed={1.4} />
    </div>

    <div class="aurora-marquee">
        <div class="aurora-marquee-track">
            {#each [...connectors, ...connectors] as c, i (i)}
                <div class="aurora-marquee-cell">
                    <img src="/logos/{c.file}" alt={c.name} />
                    <span>{c.name}</span>
                </div>
            {/each}
        </div>
    </div>

    <div class="aurora-conn-foot">
        <span>+ a new connector per release on average</span>
        <span>&middot;</span>
        <span>requests welcome</span>
    </div>
</section>

<!-- 03 Install -->
<section class="aurora-section aurora-install">
    <div class="aurora-section-head">
        <div class="aurora-section-label">&#167; 03 &mdash; Run it tonight</div>
        <h2>Five minutes from <em>git clone</em> to a dashboard.</h2>
    </div>

    <div class="aurora-terminal">
        <div class="aurora-terminal-head">
            <span>~/nocturne</span>
            <span class="aurora-terminal-dots"><i></i><i></i><i></i></span>
        </div>
        <pre
            class="aurora-terminal-body"
>$ git clone https://github.com/nightscout/nocturne
$ cd nocturne &amp;&amp; cp .env.example .env
$ docker compose up -d

  &#10003; postgres        ready in 1.2s
  &#10003; nocturne-api    ready in 0.8s
  &#10003; nocturne-web    ready in 0.4s

  &rarr; open http://localhost:5173</pre>
    </div>

    <div class="aurora-install-ctas">
        <Button href="/docs/installation" size="lg" class="gap-2">
            Installation guide <ArrowRight class="w-4 h-4" />
        </Button>
        <Button href="/docs" variant="outline" size="lg">
            Read the docs
        </Button>
    </div>
</section>

<!-- 04 Community -->
{#if communityData}
    <section class="aurora-section aurora-community">
        <div class="aurora-section-head">
            <div class="aurora-section-label">&#167; 04 &mdash; Community</div>
            <h2>Built in the open. <em>Maintained by volunteers.</em></h2>
        </div>
        <div class="aurora-community-grid">
            <div class="aurora-stat-card">
                <div class="aurora-stat-num">{communityData.stars.toLocaleString()}</div>
                <div class="aurora-stat-label">GitHub stars</div>
            </div>
            <div class="aurora-stat-card">
                <div class="aurora-stat-num">{communityData.forks.toLocaleString()}</div>
                <div class="aurora-stat-label">Forks</div>
            </div>
            <div class="aurora-stat-card">
                <div class="aurora-stat-num">{communityData.contributors.toLocaleString()}</div>
                <div class="aurora-stat-label">Contributors</div>
            </div>
            {#if communityData.latestRelease}
                <div class="aurora-stat-card">
                    <div class="aurora-stat-num">{communityData.latestRelease}</div>
                    <div class="aurora-stat-label">Latest release</div>
                </div>
            {/if}
        </div>
    </section>
{/if}

<style>
    .aurora-hero-wrap {
        position: relative;
        width: 100%;
        overflow: hidden;
        margin-top: -4rem;
    }

    .aurora-water-defs {
        position: absolute;
        width: 0;
        height: 0;
        overflow: hidden;
    }

    .aurora-grain {
        position: absolute;
        inset: 0;
        pointer-events: none;
        background-image: url("data:image/svg+xml,%3Csvg viewBox='0 0 256 256' xmlns='http://www.w3.org/2000/svg'%3E%3Cfilter id='n'%3E%3CfeTurbulence type='fractalNoise' baseFrequency='0.9' numOctaves='4' stitchTiles='stitch'/%3E%3C/filter%3E%3Crect width='100%25' height='100%25' filter='url(%23n)' opacity='0.04'/%3E%3C/svg%3E");
        background-size: 200px 200px;
        opacity: 0.35;
        mix-blend-mode: overlay;
    }

    .aurora-fade {
        position: absolute;
        bottom: 0;
        left: 0;
        right: 0;
        height: 280px;
        background: linear-gradient(to bottom, transparent, var(--background));
        pointer-events: none;
    }

    .aurora-chrome {
        position: absolute;
        inset: 0;
        padding-top: 4rem;
    }

    .aurora-stamp {
        position: absolute;
        display: flex;
        flex-direction: column;
        gap: 2px;
        font-family: ui-monospace, "SF Mono", Menlo, monospace;
        font-size: 9px;
        letter-spacing: 0.12em;
        text-transform: uppercase;
        color: oklch(0.6 0.01 261);
        top: calc(4rem + 20px);
    }

    .aurora-stamp-tl { left: 24px; }
    .aurora-stamp-tr { right: 24px; text-align: right; }

    .aurora-hero-text {
        position: absolute;
        bottom: 200px;
        left: 50%;
        transform: translateX(-50%);
        width: min(760px, 90vw);
        text-align: center;
        display: flex;
        flex-direction: column;
        align-items: center;
        gap: 20px;
    }

    .aurora-eyebrow {
        display: inline-flex;
        align-items: center;
        gap: 8px;
        font-family: ui-monospace, "SF Mono", Menlo, monospace;
        font-size: 11px;
        letter-spacing: 0.16em;
        text-transform: uppercase;
        color: oklch(0.7 0.01 261);
    }

    .aurora-eyebrow-dot {
        width: 6px;
        height: 6px;
        border-radius: 50%;
        background: var(--glucose-in-range);
        box-shadow: 0 0 0 3px color-mix(in oklch, var(--glucose-in-range), transparent 80%);
        animation: aurora-pulse 2.4s ease-in-out infinite;
        flex-shrink: 0;
    }

    @keyframes aurora-pulse {
        0%, 100% { box-shadow: 0 0 0 3px color-mix(in oklch, var(--glucose-in-range), transparent 80%); }
        50%       { box-shadow: 0 0 0 7px color-mix(in oklch, var(--glucose-in-range), transparent 92%); }
    }

    .aurora-h1 {
        display: flex;
        flex-direction: column;
        align-items: center;
        font-size: clamp(2.5rem, 7vw, 4.8rem);
        font-weight: 700;
        line-height: 1.06;
        letter-spacing: -0.025em;
        color: oklch(0.97 0.005 261);
        margin: 0;
        text-shadow: 0 2px 40px oklch(0 0 0 / 60%);
    }

    .aurora-h1 em {
        font-style: italic;
        color: var(--glucose-in-range);
    }

    .aurora-lede {
        font-size: 1.05rem;
        line-height: 1.65;
        color: oklch(0.74 0.015 261);
        max-width: 540px;
        margin: 0;
        text-shadow: 0 1px 20px oklch(0 0 0 / 50%);
    }

    .aurora-ctas {
        display: flex;
        flex-wrap: wrap;
        gap: 12px;
        justify-content: center;
    }

    :global(.aurora-btn-primary) {
        background: oklch(0.96 0.005 261) !important;
        color: oklch(0.13 0.028 261) !important;
        box-shadow: 0 0 0 1px oklch(1 0 0 / 20%) !important;
    }

    :global(.aurora-btn-ghost) {
        background: oklch(1 0 0 / 8%) !important;
        color: oklch(0.92 0.01 261) !important;
        border: 1px solid oklch(1 0 0 / 20%) !important;
        backdrop-filter: blur(8px);
    }

    .aurora-section {
        max-width: 1200px;
        margin: 0 auto;
        padding: 80px 24px;
    }

    .aurora-section-head {
        margin-bottom: 52px;
    }

    .aurora-section-label {
        font-family: ui-monospace, "SF Mono", Menlo, monospace;
        font-size: 11px;
        letter-spacing: 0.14em;
        text-transform: uppercase;
        color: var(--muted-foreground);
        margin-bottom: 16px;
    }

    .aurora-section-head h2 {
        font-size: clamp(1.6rem, 3.5vw, 2.5rem);
        font-weight: 700;
        line-height: 1.2;
        letter-spacing: -0.02em;
        color: var(--foreground);
        margin: 0;
    }

    .aurora-section-head h2 em {
        font-style: italic;
        color: var(--glucose-in-range);
    }

    .aurora-manifesto {
        border-top: 1px solid var(--border);
    }

    .aurora-cols {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
        gap: 1px;
        background: var(--border);
        border: 1px solid var(--border);
        border-radius: 12px;
        overflow: hidden;
    }

    .aurora-col {
        background: var(--background);
        padding: 36px 32px;
        display: flex;
        flex-direction: column;
        gap: 14px;
    }

    .aurora-col-num {
        font-family: ui-monospace, "SF Mono", Menlo, monospace;
        font-size: 26px;
        font-weight: 700;
        color: oklch(1 0 0 / 7%);
        line-height: 1;
        letter-spacing: -0.02em;
    }

    .aurora-col h3 {
        font-size: 1rem;
        font-weight: 600;
        color: var(--foreground);
        margin: 0;
    }

    .aurora-col p {
        font-size: 0.875rem;
        line-height: 1.65;
        color: var(--muted-foreground);
        margin: 0;
    }

    .aurora-connectors {
        border-top: 1px solid var(--border);
    }

    .aurora-conn-strip {
        width: 100%;
        border-radius: 2px;
        overflow: hidden;
        margin-bottom: 36px;
        height: 6px;
    }

    .aurora-marquee {
        overflow: hidden;
        mask-image: linear-gradient(to right, transparent, black 10%, black 90%, transparent);
        -webkit-mask-image: linear-gradient(to right, transparent, black 10%, black 90%, transparent);
        margin-bottom: 24px;
    }

    .aurora-marquee-track {
        display: flex;
        animation: aurora-scroll 40s linear infinite;
        width: max-content;
    }

    @keyframes aurora-scroll {
        from { transform: translateX(0); }
        to   { transform: translateX(-50%); }
    }

    .aurora-marquee-cell {
        display: flex;
        align-items: center;
        gap: 8px;
        padding: 10px 20px;
        border-right: 1px solid var(--border);
        flex-shrink: 0;
    }

    .aurora-marquee-cell img {
        width: 24px;
        height: 24px;
        border-radius: 5px;
        object-fit: cover;
    }

    .aurora-marquee-cell span {
        font-size: 13px;
        font-weight: 500;
        color: var(--muted-foreground);
        white-space: nowrap;
    }

    .aurora-conn-foot {
        display: flex;
        gap: 10px;
        align-items: center;
        font-family: ui-monospace, "SF Mono", Menlo, monospace;
        font-size: 11px;
        color: var(--muted-foreground);
    }

    .aurora-install {
        border-top: 1px solid var(--border);
    }

    .aurora-terminal {
        background: oklch(0.10 0.025 261);
        border: 1px solid var(--border);
        border-radius: 12px;
        overflow: hidden;
        margin-bottom: 36px;
        max-width: 680px;
    }

    .aurora-terminal-head {
        display: flex;
        align-items: center;
        justify-content: space-between;
        padding: 10px 16px;
        background: oklch(0.16 0.03 261);
        border-bottom: 1px solid var(--border);
        font-family: ui-monospace, "SF Mono", Menlo, monospace;
        font-size: 12px;
        color: var(--muted-foreground);
    }

    .aurora-terminal-dots {
        display: flex;
        gap: 5px;
    }

    .aurora-terminal-dots i {
        display: block;
        width: 10px;
        height: 10px;
        border-radius: 50%;
        background: oklch(1 0 0 / 15%);
    }

    .aurora-terminal-body {
        padding: 20px;
        font-family: ui-monospace, "SF Mono", Menlo, monospace;
        font-size: 13px;
        line-height: 1.7;
        color: oklch(0.82 0.03 261);
        margin: 0;
        white-space: pre;
        overflow-x: auto;
    }

    .aurora-install-ctas {
        display: flex;
        flex-wrap: wrap;
        gap: 12px;
    }

    .aurora-community {
        border-top: 1px solid var(--border);
    }

    .aurora-community-grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
        gap: 1px;
        background: var(--border);
        border: 1px solid var(--border);
        border-radius: 12px;
        overflow: hidden;
    }

    .aurora-stat-card {
        background: var(--background);
        padding: 32px 28px;
        display: flex;
        flex-direction: column;
        gap: 6px;
    }

    .aurora-stat-num {
        font-size: 2rem;
        font-weight: 700;
        letter-spacing: -0.03em;
        color: var(--foreground);
        font-variant-numeric: tabular-nums;
    }

    .aurora-stat-label {
        font-size: 0.78rem;
        color: var(--muted-foreground);
        font-family: ui-monospace, "SF Mono", Menlo, monospace;
        letter-spacing: 0.05em;
        text-transform: uppercase;
    }
</style>
