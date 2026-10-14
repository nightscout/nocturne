<script lang="ts">
  import { deferredArtwork } from "$lib/watercolour-deferral.svelte";

  interface Props {
    /** Seeds the wash, so each report keeps its own. */
    name: string;
    class?: string;
  }

  let { name, class: className }: Props = $props();

  // The wash boots the engine as it mounts; the header reads the same without it.
  const wash = deferredArtwork(() => import("$lib/watercolour/report-header-wash"));
</script>

{#if wash.component}
  <wash.component {name} position="absolute" class={className} />
{/if}
