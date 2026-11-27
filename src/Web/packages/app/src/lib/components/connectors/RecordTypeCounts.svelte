<script lang="ts">
  import { dataTypeLabel } from "#lib/utils/data-type-labels.svelte.js";
  import { formatNumber } from "#lib/utils/formatting.js";

  let { counts }: { counts: [string, number][] } = $props();
</script>

<ul class="space-y-1">
  {#each counts as [type, count] (type)}
    <li class="flex justify-between gap-4">
      <span>{dataTypeLabel(type)}</span>
      <span class="tabular-nums">{formatNumber(count)}</span>
    </li>
  {/each}
</ul>
