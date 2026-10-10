<script lang="ts">
  import prefetchScript from "$lib/stores/initial-reads.prefetch.js?raw";

  let { enabled }: { enabled: boolean } = $props();

  // Split so the closing tag does not end this component's own script block.
  const markup = `<script>${prefetchScript}</${"script"}>`;
</script>

<svelte:head>
  {#if enabled}
    <!-- eslint-disable-next-line svelte/no-at-html-tags -- a bundled source file, no user input -->
    {@html markup}
  {/if}
</svelte:head>
