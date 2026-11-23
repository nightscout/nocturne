<script lang="ts">
  import { scopeLabels } from "#lib/constants/scope-labels.svelte.js";

  let { permissions = [] }: { permissions: string[] } = $props();

  const labels = $derived<Record<string, string>>(scopeLabels());
</script>

{#if permissions.length === 0}
  <p class="text-sm text-muted-foreground">This role gives no permissions.</p>
{:else}
  <ul class="space-y-0.5 text-sm">
    {#each permissions as permission, index (index)}
      <li>{labels[permission] ?? permission}</li>
    {/each}
  </ul>
{/if}
