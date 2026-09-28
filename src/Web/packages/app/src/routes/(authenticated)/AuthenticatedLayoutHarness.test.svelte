<script lang="ts">
  import type { Component, ComponentProps, Snippet } from "svelte";
  import Layout from "./+layout.svelte";

  const {
    data,
    section,
  }: {
    data: ComponentProps<typeof Layout>["data"];
    section?: Component<{ children: Snippet }>;
  } = $props();
</script>

{#snippet content()}
  <p>page content</p>
{/snippet}

<Layout {data}>
  {#if section}
    {@const Section = section}
    <Section children={content} />
  {:else}
    {@render content()}
  {/if}
</Layout>
