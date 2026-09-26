<script lang="ts">
    import { ArtworkHero } from "@nocturne/watercolour";
    import Content from "../../../content/docs/getting-started.svx";
    import { Button } from "@nocturne/ui/ui/button";
    import { DOCS_HERO } from "$lib/data/docs-artwork";
    import { ArrowRight } from "@lucide/svelte";

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

    <div class="mt-12 flex gap-4 not-prose">
        <Button href="/docs/installation" variant="outline">
            Full Installation Guide
            <ArrowRight class="w-4 h-4" />
        </Button>
    </div>
</div>
