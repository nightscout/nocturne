<script lang="ts">
    import { ArtworkHero } from "water-ui";
    import Content from "../../../content/docs/getting-started.svx";
    import { Button } from "@nocturne/ui/ui/button";
    import ArrowRight from "@lucide/svelte/icons/arrow-right";
    import { DOCS_HERO } from "#lib/data/docs-artwork.js";
    import { hosting } from "#lib/components/docs/hosting/hosting.svelte.js";

    const hero = DOCS_HERO["getting-started"];
</script>

<div class="max-w-3xl">
    <ArtworkHero
        artwork={hero.artwork}
        palette={hero.palette}
        motion="auto"
        autoplay="once"
        size={400}
        wrap={hero.wrap ?? true}
    />
    <Content />

    <div class="mt-12 flex flex-wrap gap-4 not-prose">
        {#if hosting.shows("managed")}
            <Button href="/docs/managed-service" variant="outline">
                Managed service providers
                <ArrowRight class="w-4 h-4" />
            </Button>
        {/if}
        {#if hosting.shows("self-host")}
            <Button href="/docs/installation" variant="outline">
                Full installation guide
                <ArrowRight class="w-4 h-4" />
            </Button>
        {/if}
    </div>
</div>
