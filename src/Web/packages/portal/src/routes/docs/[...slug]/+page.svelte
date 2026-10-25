<script lang="ts">
    import { ArtworkHero } from "@nocturne/watercolour";
    import { DOCS_HERO } from "$lib/data/docs-artwork";

    let { data } = $props();

    const hero = $derived(DOCS_HERO[data.slug]);
</script>

<div class="max-w-3xl">
    {#if hero}
        <ArtworkHero
            artwork={hero.artwork}
            palette={hero.palette}
            motion="auto"
            autoplay="once"
            size={400}
            wrap={hero.wrap ?? true}
        />
    {/if}
    <data.content />
</div>
