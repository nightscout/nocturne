<script lang="ts">
    import { Button } from "@nocturne/ui/ui/button";
    import { ArrowRight, Play } from "@lucide/svelte";
    import { DEMO_ENABLED } from "$lib/config";
    import AuroraCanvas from "$lib/components/AuroraCanvas.svelte";
    import AuroraPool from "$lib/components/AuroraPool.svelte";
    import FeaturePillars from "$lib/components/features/FeaturePillars.svelte";
    import { getCommunityData } from "$lib/data/portal";
    import { DATA_SOURCES, LIVE_CONNECTORS } from "$lib/data/connectors";
    import { AVAILABLE_REPORT_COUNT } from "$lib/data/reports";
    import { FlowField } from "$lib/utils/aurora-flow";

    let textBlockEl: HTMLElement | null = $state(null);
    const flow = new FlowField();

    let communityData = $state<Awaited<ReturnType<typeof getCommunityData>> | null>(null);
    getCommunityData()
        .then((d) => (communityData = d))
        .catch(() => {});

    const MAX_AVATARS = 20;
    let topContributors = $derived(
        communityData
            ? [...communityData.contributors]
                  .sort((a, b) => b.contributions - a.contributions)
                  .slice(0, MAX_AVATARS)
            : []
    );
    let contributorOverflow = $derived(
        communityData ? Math.max(0, communityData.contributors.length - MAX_AVATARS) : 0
    );

</script>

<!-- Hero -->
<section class="relative w-full overflow-hidden -mt-16">
    <AuroraCanvas height={920} {flow} />
    <div class="grain-bg absolute inset-0 pointer-events-none opacity-35 mix-blend-overlay" aria-hidden="true"></div>
    <AuroraPool textBlock={textBlockEl} {flow} />
    <div class="absolute bottom-0 inset-x-0 h-[280px] pointer-events-none bg-gradient-to-b from-transparent to-background" aria-hidden="true"></div>

    <div class="absolute inset-0 pt-16 pointer-events-none">
        <div class="absolute top-[calc(4rem+20px)] left-6 hidden md:flex flex-col gap-0.5 font-mono text-2xs tracking-eyebrow uppercase text-muted-foreground">
            <span>NCTRN / HOMEPAGE</span>
            <span>{communityData?.latestRelease ?? "preview"} &middot; public preview</span>
        </div>
        <div class="absolute top-[calc(4rem+20px)] right-6 hidden md:flex flex-col gap-0.5 font-mono text-2xs tracking-eyebrow uppercase text-muted-foreground text-right">
            <span>{DATA_SOURCES.length} data sources</span>
            <span>{AVAILABLE_REPORT_COUNT} reports</span>
        </div>

        <div
            bind:this={textBlockEl}
            class="absolute bottom-[200px] left-1/2 -translate-x-1/2 w-[min(760px,90vw)] text-center flex flex-col items-center gap-5"
        >
            <span class="inline-flex items-center gap-2 font-mono text-xs tracking-eyebrow uppercase text-foreground/95 text-shadow-hero-sm">
                <span class="eyebrow-dot size-1.5 rounded-full shrink-0 bg-glucose-in-range"></span>Nightscout-compatible, rebuilt
            </span>
            <h1 class="flex flex-col items-center text-display font-bold text-foreground m-0 text-shadow-hero">
                <span>Every reading.</span>
                <span><em class="text-glucose-low">Every</em> source.</span>
                <span>One dashboard.</span>
            </h1>
            <p class="text-lead text-foreground/90 max-w-[540px] m-0 text-shadow-hero-sm">
                Nocturne pulls every CGM, pump, and app you use into one
                self-hosted dashboard. Real-time, multitenant, open source, and
                built by the diabetes community.
            </p>
            <div class="flex flex-wrap gap-3 justify-center pointer-events-auto">
                <Button href="/docs/installation" size="cta">
                    Get started <ArrowRight class="w-4 h-4" />
                </Button>
                {#if DEMO_ENABLED}
                    <Button href="/demo" variant="outline" size="cta">
                        <Play class="w-4 h-4" /> See a real day
                    </Button>
                {:else}
                    <Button href="/features" variant="outline" size="cta">
                        Explore features
                    </Button>
                {/if}
            </div>
        </div>
    </div>
</section>

<!-- 01 Manifesto -->
<section class="max-w-[1200px] mx-auto px-6 py-20 border-t border-border">
    <div class="mb-[52px]">
        <div class="font-brand text-xs font-bold tracking-eyebrow uppercase text-muted-foreground mb-4">01 &middot; Why this exists</div>
        <h2 class="text-section font-bold text-foreground m-0">
            A decade of Nightscout taught us what the data needs.<br />
            <em class="text-glucose-in-range">Nocturne is the rebuild.</em>
        </h2>
    </div>

    <div class="grid grid-cols-[repeat(auto-fit,minmax(240px,1fr))] gap-px bg-border border border-border rounded-xl overflow-hidden">
        <div class="bg-background py-9 px-8 flex flex-col gap-3.5">
            <div class="font-mono text-2xl font-bold text-foreground/7 leading-none tracking-tight">01</div>
            <h3 class="text-base font-semibold text-foreground m-0">Drop-in Nightscout API</h3>
            <p class="text-sm leading-relaxed text-muted-foreground m-0">
                Speaks the Nightscout v1, v2, and v3 APIs. The apps, watch faces, and
                followers you use today keep working; you change the URL they point at.
            </p>
        </div>
        <div class="bg-background py-9 px-8 flex flex-col gap-3.5">
            <div class="font-mono text-2xl font-bold text-foreground/7 leading-none tracking-tight">02</div>
            <h3 class="text-base font-semibold text-foreground m-0">Multitenant, by default</h3>
            <p class="text-sm leading-relaxed text-muted-foreground m-0">
                One install, many people. Run a household, a clinic, or a community
                on a single deployment, with each person's data and settings kept apart.
            </p>
        </div>
        <div class="bg-background py-9 px-8 flex flex-col gap-3.5">
            <div class="font-mono text-2xl font-bold text-foreground/7 leading-none tracking-tight">03</div>
            <h3 class="text-base font-semibold text-foreground m-0">Real-time by design</h3>
            <p class="text-sm leading-relaxed text-muted-foreground m-0">
                Built around WebSockets and PostgreSQL. A new reading reaches every open
                dashboard and follower the moment it lands, and years of history stay quick to browse.
            </p>
        </div>
    </div>
</section>

<!-- 02 Connectors -->
<section class="max-w-[1200px] mx-auto px-6 py-20 border-t border-border">
    <div class="mb-[52px]">
        <div class="font-brand text-xs font-bold tracking-eyebrow uppercase text-muted-foreground mb-4">02 &middot; What plugs in</div>
        <h2 class="text-section font-bold text-foreground m-0">{DATA_SOURCES.length} sources. <em class="text-glucose-in-range">One dashboard.</em></h2>
    </div>

    <div class="w-full rounded-sm overflow-hidden mb-9 h-1.5">
        <AuroraCanvas height={6} intensity={1.2} speed={1.4} />
    </div>

    <div class="marquee-mask overflow-hidden mb-6">
        <div class="marquee-track flex w-max">
            {#each [...LIVE_CONNECTORS, ...LIVE_CONNECTORS] as c, i (i)}
                <div class="flex items-center gap-2 px-5 py-2.5 border-r border-border shrink-0">
                    <img src="/logos/{c.file}" alt={c.name} class="size-6 rounded-sm object-cover" />
                    <span class="text-sm font-medium text-muted-foreground whitespace-nowrap">{c.name}</span>
                </div>
            {/each}
        </div>
    </div>

    <div class="flex gap-2.5 items-center font-mono text-xs text-muted-foreground">
        <span>Missing yours?</span>
        <span>&middot;</span>
        <a href="https://github.com/nightscout/nocturne/issues/new" class="underline-offset-2 hover:underline hover:text-foreground" target="_blank" rel="noopener noreferrer">Ask for a connector</a>
    </div>
</section>

<!-- 03 Features -->
<FeaturePillars demoHeight={400} />

<!-- 04 Install -->
<section class="max-w-[1200px] mx-auto px-6 py-20 border-t border-border">
    <div class="mb-[52px]">
        <div class="font-brand text-xs font-bold tracking-eyebrow uppercase text-muted-foreground mb-4">04 &middot; Run it tonight</div>
        <h2 class="text-section font-bold text-foreground m-0">Two files and a domain name. <em class="text-glucose-in-range">That's the install.</em></h2>
    </div>

    <div class="bg-sunken border border-border rounded-xl overflow-hidden mb-9 max-w-[680px]">
        <div class="flex items-center justify-between px-4 py-2.5 bg-card/50 border-b border-border font-mono text-xs text-muted-foreground">
            <span>~/nocturne</span>
            <span class="flex gap-1.25">
                <i class="block size-2.5 rounded-full bg-foreground/15"></i>
                <i class="block size-2.5 rounded-full bg-foreground/15"></i>
                <i class="block size-2.5 rounded-full bg-foreground/15"></i>
            </span>
        </div>
        <pre
            class="p-5 font-mono text-sm leading-relaxed text-foreground/80 m-0 whitespace-pre overflow-x-auto"
>$ mkdir nocturne &amp;&amp; cd nocturne
$ curl -LO https://github.com/nightscout/nocturne/releases/latest/download/docker-compose.yaml
$ curl -L -o .env https://github.com/nightscout/nocturne/releases/latest/download/default.env.example
$ nano .env      # BASE_DOMAIN, INSTANCE_KEY, four database passwords
$ docker compose up -d

  &#10003; postgres        healthy
  &#10003; nocturne-api    healthy
  &#10003; nocturne-web    healthy
  &#10003; caddy           certificate issued

  &rarr; open https://example.com</pre>
    </div>

    <div class="flex flex-wrap gap-3">
        <Button href="/docs/installation" size="lg">
            Installation guide <ArrowRight class="w-4 h-4" />
        </Button>
        <Button href="/docs" variant="outline" size="lg">
            Read the docs
        </Button>
    </div>
</section>

<!-- 05 Community -->
{#if communityData}
    <section class="max-w-[1200px] mx-auto px-6 py-20 border-t border-border">
        <div class="mb-[52px]">
            <div class="font-brand text-xs font-bold tracking-eyebrow uppercase text-muted-foreground mb-4">05 &middot; Community</div>
            <h2 class="text-section font-bold text-foreground m-0">Built in the open. <em class="text-glucose-in-range">Maintained by volunteers.</em></h2>
        </div>

        <div class="grid grid-cols-[repeat(auto-fit,minmax(160px,1fr))] gap-px bg-border border border-border rounded-xl overflow-hidden">
            <div class="bg-background py-8 px-7 flex flex-col gap-1.5">
                <div class="text-3xl font-bold tracking-tight text-foreground tabular-nums">{communityData.stars.toLocaleString()}</div>
                <div class="text-xs text-muted-foreground font-mono tracking-wider uppercase">GitHub stars</div>
            </div>
            <div class="bg-background py-8 px-7 flex flex-col gap-1.5">
                <div class="text-3xl font-bold tracking-tight text-foreground tabular-nums">{communityData.forks.toLocaleString()}</div>
                <div class="text-xs text-muted-foreground font-mono tracking-wider uppercase">Forks</div>
            </div>
            {#if communityData.latestRelease}
                <div class="bg-background py-8 px-7 flex flex-col gap-1.5">
                    <div class="text-3xl font-bold tracking-tight text-foreground tabular-nums">{communityData.latestRelease}</div>
                    <div class="text-xs text-muted-foreground font-mono tracking-wider uppercase">Latest release</div>
                </div>
            {/if}
        </div>

        <div class="flex items-center gap-4 mt-10">
            <div class="flex items-center">
                {#each topContributors as c (c.login)}
                    <a
                        href={c.html_url}
                        target="_blank"
                        rel="external noopener noreferrer"
                        title="{c.login} &middot; {c.contributions} commits"
                        class="-ml-2.5 first:ml-0 relative block rounded-full border-2 border-background transition-transform duration-150 z-[1] hover:-translate-y-[3px] hover:scale-110 hover:z-10"
                    >
                        <img src="{c.avatar_url}&s=80" alt={c.login} width="36" height="36" loading="lazy" class="block size-9 rounded-full" />
                    </a>
                {/each}
                {#if contributorOverflow > 0}
                    <div class="-ml-2.5 size-9 rounded-full bg-muted border-2 border-background flex items-center justify-center text-2xs font-semibold text-muted-foreground font-mono shrink-0">+{contributorOverflow}</div>
                {/if}
            </div>
            <span class="font-mono text-xs tracking-widest uppercase text-muted-foreground whitespace-nowrap
">
                {communityData.contributors.length} contributors
            </span>
        </div>
    </section>
{/if}

<style>
    /* SVG grain: a data URI can't be expressed as a Tailwind utility */
    .grain-bg {
        background-image: url("data:image/svg+xml,%3Csvg viewBox='0 0 256 256' xmlns='http://www.w3.org/2000/svg'%3E%3Cfilter id='n'%3E%3CfeTurbulence type='fractalNoise' baseFrequency='0.9' numOctaves='4' stitchTiles='stitch'/%3E%3C/filter%3E%3Crect width='100%25' height='100%25' filter='url(%23n)' opacity='0.04'/%3E%3C/svg%3E");
        background-size: 200px 200px;
    }

    /* Eyebrow dot: keyframe + color-mix box-shadow */
    @keyframes aurora-pulse {
        0%, 100% { box-shadow: 0 0 0 3px color-mix(in oklch, var(--glucose-in-range), transparent 80%); }
        50%       { box-shadow: 0 0 0 7px color-mix(in oklch, var(--glucose-in-range), transparent 92%); }
    }
    .eyebrow-dot {
        box-shadow: 0 0 0 3px color-mix(in oklch, var(--glucose-in-range), transparent 80%);
        animation: aurora-pulse 2.4s ease-in-out infinite;
    }

    /* Marquee: keyframe + vendor-prefixed mask */
    @keyframes aurora-scroll {
        from { transform: translateX(0); }
        to   { transform: translateX(-50%); }
    }
    .marquee-track { animation: aurora-scroll 40s linear infinite; }
    .marquee-mask {
        mask-image: linear-gradient(to right, transparent, black 10%, black 90%, transparent);
        -webkit-mask-image: linear-gradient(to right, transparent, black 10%, black 90%, transparent);
    }
</style>
