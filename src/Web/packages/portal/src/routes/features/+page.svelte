<script lang="ts">
    import { Artwork } from "@nocturne/watercolour";
    import { ArrowRight, Play } from "@lucide/svelte";
    import { Button } from "@nocturne/ui/ui/button";
    import FeaturePillars from "$lib/components/features/FeaturePillars.svelte";
    import { DATA_SOURCES } from "$lib/data/connectors";
    import { AVAILABLE_REPORT_COUNT } from "$lib/data/reports";

    const SUPPORTING = [
        {
            title: "Your server, your data",
            copy: "Self-hosted with Docker Compose, Portainer, or Helm. No cloud middleman and no third-party analytics. Your health data never leaves your infrastructure.",
        },
        {
            title: "One install, many people",
            copy: "Run a household, a clinic, or a community on a single deployment. Each tenant gets its own subdomain, and row-level security in the database keeps one tenant's data out of another's.",
        },
        {
            title: "Real-time updates",
            copy: "New readings reach every open dashboard and follower the moment they are written, over WebSockets rather than polling.",
        },
        {
            title: "Drop-in Nightscout",
            copy: "Speaks the Nightscout v1, v2, and v3 APIs. xDrip+, Loop, AndroidAPS, Trio, and watch faces keep working after you switch.",
        },
        {
            title: "Built for years of data",
            copy: "PostgreSQL underneath, with the reports written to query years of readings. Jumping to a date from years ago stays quick.",
        },
        {
            title: "Free and open source",
            copy: "AGPL-3.0 licensed, so you can read it, fork it, and self-host it for nothing. Stewarded by the Nightscout Foundation.",
        },
    ];

    const COMPARISON = [
        {
            aspect: "Who it serves",
            nightscout: "One instance per person",
            nocturne: "One install for a household, a clinic, or a community",
        },
        {
            aspect: "Getting data in",
            nightscout: "Uploader apps or bridge plugins you configure",
            nocturne: `Sign in to ${DATA_SOURCES.length} sources from the dashboard; uploaders still work`,
        },
        {
            aspect: "Alarms",
            nightscout: "High and low thresholds, with a snooze",
            nocturne: "Rules with thresholds, durations, and trends, delivered anywhere",
        },
        {
            aspect: "Access",
            nightscout: "One shared API secret for every app and follower",
            nocturne: "Passkeys and social sign-in, with a scoped token per app",
        },
        {
            aspect: "Reports",
            nightscout: "A fixed set of report pages",
            nocturne: `${AVAILABLE_REPORT_COUNT} reports, with your own target range drawn on them`,
        },
    ];
</script>

<div class="relative max-w-[1200px] mx-auto px-6 overflow-x-clip">
    <!-- The copy caps at 900px, so on a wide screen the paint takes the
         rest of the band rather than shrinking into a well. -->
    <Artwork
        artwork="heart-rate"
        palette="ember"
        motion="auto"
        autoplay="once"
        position="absolute"
        class="pointer-events-none -right-12 top-10 hidden size-[380px] opacity-90 lg:block xl:size-[440px]"
    />
    <div class="pt-20 pb-16 flex flex-col gap-6 max-w-[900px]">
        <h1 class="text-display font-bold text-foreground m-0">Everything Nocturne does</h1>
        <p class="text-lead text-muted-foreground m-0 max-w-[680px]">
            Nocturne is a free, open-source diabetes dashboard you run on your own
            server. It connects to your CGM, pump, and apps; shows your numbers
            the way you want; and tells you the moment something needs attention.
        </p>
        <div class="flex flex-wrap gap-3 mt-2">
            <Button href="/docs/installation" size="cta">
                Get started <ArrowRight class="size-4" />
            </Button>
            <Button href="/demo" variant="outline" size="cta">
                <Play class="size-4" /> See a real day
            </Button>
        </div>
    </div>
</div>

<FeaturePillars demoHeight={440} standalone={false} />

<div class="max-w-[1200px] mx-auto px-6">
    <section class="py-20 border-t border-border grid gap-10 md:grid-cols-[minmax(0,4fr)_minmax(0,8fr)] md:gap-16">
        <h2 class="text-section font-bold text-foreground m-0">Also inside</h2>
        <dl class="m-0 border-t border-border">
            {#each SUPPORTING as item (item.title)}
                <div class="grid gap-2 py-6 border-b border-border sm:grid-cols-[13rem_1fr] sm:gap-8">
                    <dt class="font-semibold text-foreground">{item.title}</dt>
                    <dd class="m-0 leading-relaxed text-muted-foreground">{item.copy}</dd>
                </div>
            {/each}
        </dl>
    </section>

    <section class="py-20 border-t border-border">
        <h2 class="text-section font-bold text-foreground m-0 mb-4">Coming from Nightscout</h2>
        <p class="text-lead text-muted-foreground m-0 mb-10 max-w-[640px]">
            The same data, and the same API your apps already speak. What changes
            is everything around it.
        </p>

        <div class="overflow-x-auto">
            <table class="w-full min-w-[640px] border-collapse text-left">
                <thead>
                    <tr class="border-b border-border">
                        <th scope="col" class="w-[22%] py-3 pr-6"><span class="sr-only">Aspect</span></th>
                        <th scope="col" class="w-[39%] py-3 pr-6 font-semibold text-muted-foreground">Nightscout</th>
                        <th scope="col" class="w-[39%] py-3 font-semibold text-foreground">Nocturne</th>
                    </tr>
                </thead>
                <tbody>
                    {#each COMPARISON as row (row.aspect)}
                        <tr class="border-b border-border align-top">
                            <th scope="row" class="py-5 pr-6 font-medium text-foreground">{row.aspect}</th>
                            <td class="py-5 pr-6 text-muted-foreground">{row.nightscout}</td>
                            <td class="py-5 text-foreground/90">{row.nocturne}</td>
                        </tr>
                    {/each}
                </tbody>
            </table>
        </div>
    </section>

    <section class="border-t border-border py-20 max-w-[680px]">
        <h2 class="text-headline font-bold text-foreground m-0 mb-4">Start with the demo</h2>
        <p class="text-lead text-muted-foreground m-0 mb-8">
            Poke around the demo, then run your own copy with Docker Compose.
            No account, no credit card, no waitlist.
        </p>
        <div class="flex flex-wrap gap-3">
            <Button href="/docs/installation" size="cta">
                Installation guide <ArrowRight class="size-4" />
            </Button>
            <Button href="/demo" variant="outline" size="cta">
                <Play class="size-4" /> See the demo
            </Button>
            <Button href="/docs" variant="ghost" size="cta">
                Read the docs
            </Button>
        </div>
    </section>
</div>
