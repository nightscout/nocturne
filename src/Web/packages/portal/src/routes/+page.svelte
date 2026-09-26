<script lang="ts">
    import { Artwork } from "@nocturne/watercolour";
    import { Button } from "@nocturne/ui/ui/button";
    import { ArrowRight, Play } from "@lucide/svelte";
    import { DEMO_ENABLED } from "$lib/config";
    import AuroraCanvas from "$lib/components/AuroraCanvas.svelte";
    import AuroraPool from "$lib/components/AuroraPool.svelte";
    import FeaturePillars from "$lib/components/features/FeaturePillars.svelte";
    import { getCommunityData } from "$lib/data/portal";
    import { DATA_SOURCES } from "$lib/data/connectors";
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

    const UPLOADER_KINDS = new Set(["Looping", "App"]);
    const CONNECTED = DATA_SOURCES.filter((c) => !UPLOADER_KINDS.has(c.kind));
    const UPLOADERS = DATA_SOURCES.filter((c) => UPLOADER_KINDS.has(c.kind));

    const REASONS = [
        {
            title: "Drop-in Nightscout API",
            body: "Speaks the Nightscout v1, v2, and v3 APIs. The apps, watch faces, and followers you use today keep working; you change the URL they point at.",
        },
        {
            title: "Multitenant, by default",
            body: "One install, many people. Run a household, a clinic, or a community on a single deployment, with each person's data and settings kept apart.",
        },
        {
            title: "Real-time by design",
            body: "Built around WebSockets and PostgreSQL. A new reading reaches every open dashboard and follower the moment it lands, and years of history stay quick to browse.",
        },
    ];
</script>

<section class="relative w-full overflow-hidden -mt-16">
    <AuroraCanvas height={920} {flow} />
    <div class="grain-bg absolute inset-0 pointer-events-none opacity-35 mix-blend-overlay" aria-hidden="true"></div>
    <AuroraPool textBlock={textBlockEl} {flow} />
    <div class="absolute bottom-0 inset-x-0 h-[280px] pointer-events-none bg-gradient-to-b from-transparent to-background" aria-hidden="true"></div>

    <div class="absolute inset-0 pt-16 pointer-events-none">
        <div
            bind:this={textBlockEl}
            class="absolute bottom-[200px] left-1/2 -translate-x-1/2 w-[min(760px,90vw)] text-center flex flex-col items-center gap-6"
        >
            <h1 class="flex flex-col items-center text-display font-bold text-foreground m-0 text-shadow-hero text-balance">
                <span>Every reading.</span>
                <span>Every source.</span>
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

<section class="max-w-[1200px] mx-auto px-6 py-20 border-t border-border grid gap-10 md:grid-cols-[minmax(0,5fr)_minmax(0,7fr)] md:gap-16">
    <h2 class="text-section font-bold text-foreground m-0 text-balance">
        A decade of Nightscout taught us what the data needs. Nocturne is the rebuild.
    </h2>
    <dl class="m-0 border-t border-border">
        {#each REASONS as r (r.title)}
            <div class="grid gap-2 py-6 border-b border-border sm:grid-cols-[13rem_1fr] sm:gap-8">
                <dt class="font-semibold text-foreground">{r.title}</dt>
                <dd class="m-0 leading-relaxed text-muted-foreground">{r.body}</dd>
            </div>
        {/each}
    </dl>
</section>

<section class="max-w-[1200px] mx-auto px-6 py-20 border-t border-border">
    <h2 class="text-section font-bold text-foreground m-0 mb-4">{DATA_SOURCES.length} sources, one dashboard</h2>
    <p class="text-lead text-muted-foreground m-0 mb-12 max-w-[640px]">
        Sign in to a device account and readings arrive by themselves. Anything
        that already uploads to Nightscout uploads to Nocturne.
    </p>

    <div class="grid gap-10 md:grid-cols-[13rem_1fr] md:gap-x-8 md:gap-y-12">
        <h3 class="text-base font-semibold text-foreground m-0">Connected accounts</h3>
        <ul class="m-0 p-0 list-none flex flex-wrap gap-x-8 gap-y-4">
            {#each CONNECTED as c (c.name)}
                <li class="flex items-center gap-2.5">
                    <img src="/logos/{c.file}" alt="" class="size-6 rounded-sm object-cover" />
                    <span class="text-foreground/85">{c.name}</span>
                </li>
            {/each}
        </ul>

        <h3 class="text-base font-semibold text-foreground m-0">Nightscout uploaders</h3>
        <ul class="m-0 p-0 list-none flex flex-wrap gap-x-8 gap-y-4">
            {#each UPLOADERS as c (c.name)}
                <li class="flex items-center gap-2.5">
                    <img src="/logos/{c.file}" alt="" class="size-6 rounded-sm object-cover" />
                    <span class="text-foreground/85">{c.name}</span>
                </li>
            {/each}
        </ul>
    </div>

    <p class="text-sm text-muted-foreground mt-12 mb-0">
        Missing yours?
        <a href="https://github.com/nightscout/nocturne/issues/new" class="text-foreground underline underline-offset-4 decoration-border hover:decoration-foreground" target="_blank" rel="noopener noreferrer">Ask for a connector</a>.
    </p>
</section>

<FeaturePillars demoHeight={400} />

<section class="max-w-[1200px] mx-auto px-6 py-20 border-t border-border grid gap-10 md:grid-cols-[minmax(0,5fr)_minmax(0,7fr)] md:gap-16">
    <div class="flex flex-col gap-6">
        <h2 class="text-section font-bold text-foreground m-0 text-balance">Two files and a domain name. That's the install.</h2>
        <p class="text-lead text-muted-foreground m-0">
            Download the compose file and its environment template, fill in your
            domain and passwords, and start it. Caddy fetches the certificate.
        </p>
        <div class="flex flex-wrap gap-3">
            <Button href="/docs/installation" size="lg">
                Installation guide <ArrowRight class="w-4 h-4" />
            </Button>
            <Button href="/docs" variant="outline" size="lg">
                Read the docs
            </Button>
        </div>
        <Artwork
            artwork="key"
            palette="ember"
            motion="auto"
            autoplay="once"
            class="hidden size-[200px] shrink-0 lg:block xl:size-[240px]"
        />
    </div>

    <pre
        class="m-0 p-6 bg-sunken border border-border rounded-lg font-mono text-sm leading-loose text-foreground/85 whitespace-pre overflow-x-auto self-start"
><span class="text-muted-foreground">$</span> mkdir nocturne &amp;&amp; cd nocturne
<span class="text-muted-foreground">$</span> curl -LO https://github.com/nightscout/nocturne/releases/latest/download/docker-compose.yaml
<span class="text-muted-foreground">$</span> curl -L -o .env https://github.com/nightscout/nocturne/releases/latest/download/default.env.example
<span class="text-muted-foreground">$</span> nano .env   <span class="text-muted-foreground"># BASE_DOMAIN, INSTANCE_KEY, four database passwords</span>
<span class="text-muted-foreground">$</span> docker compose up -d</pre>
</section>

{#if communityData}
    <section class="max-w-[1200px] mx-auto px-6 py-20 border-t border-border">
        <h2 class="text-section font-bold text-foreground m-0 mb-4">Built in the open, maintained by volunteers</h2>
        <p class="text-lead text-muted-foreground m-0 max-w-[640px]">
            <span class="text-foreground tabular-nums">{communityData.contributors.length}</span> people have contributed so far.
            The repository has
            <span class="text-foreground tabular-nums">{communityData.stars.toLocaleString()}</span> stars and
            <span class="text-foreground tabular-nums">{communityData.forks.toLocaleString()}</span> forks{#if communityData.latestRelease}, and the latest release is
                <span class="text-foreground tabular-nums">{communityData.latestRelease}</span>{/if}.
        </p>

        <div class="flex items-center mt-10">
            {#each topContributors as c (c.login)}
                <a
                    href={c.html_url}
                    target="_blank"
                    rel="external noopener noreferrer"
                    title="{c.login} &middot; {c.contributions} commits"
                    class="-ml-2.5 first:ml-0 relative block rounded-full border-2 border-background transition-transform duration-150 z-[1] hover:-translate-y-[3px] hover:z-10 motion-reduce:transition-none motion-reduce:hover:translate-y-0"
                >
                    <img src="{c.avatar_url}&s=80" alt={c.login} width="36" height="36" loading="lazy" class="block size-9 rounded-full" />
                </a>
            {/each}
            {#if contributorOverflow > 0}
                <div class="-ml-2.5 size-9 rounded-full bg-muted border-2 border-background flex items-center justify-center text-2xs font-semibold text-muted-foreground tabular-nums shrink-0">+{contributorOverflow}</div>
            {/if}
        </div>
    </section>
{/if}

<style>
    /* SVG grain: a data URI can't be expressed as a Tailwind utility */
    .grain-bg {
        background-image: url("data:image/svg+xml,%3Csvg viewBox='0 0 256 256' xmlns='http://www.w3.org/2000/svg'%3E%3Cfilter id='n'%3E%3CfeTurbulence type='fractalNoise' baseFrequency='0.9' numOctaves='4' stitchTiles='stitch'/%3E%3C/filter%3E%3Crect width='100%25' height='100%25' filter='url(%23n)' opacity='0.04'/%3E%3C/svg%3E");
        background-size: 200px 200px;
    }
</style>
