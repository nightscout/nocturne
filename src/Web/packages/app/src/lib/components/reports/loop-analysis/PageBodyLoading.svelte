<!--
  A Loop Analysis page's body while its first response loads: the figure strip and two panels.
  The shared filters and page links above it stay on screen.
-->
<script lang="ts">
  import { Skeleton } from "#lib/components/ui/skeleton/index.js";
</script>

<div class="space-y-6" role="status" aria-label="Loading">
  <div class="grid grid-cols-2 gap-px overflow-hidden rounded-lg border @md:grid-cols-6">
    {#each Array.from({ length: 6 }, (_, i) => i) as i (i)}
      <div class="space-y-2 bg-card p-3">
        <Skeleton class="h-3 w-24" />
        <Skeleton class="h-6 w-16" />
        <Skeleton class="h-3 w-full" />
      </div>
    {/each}
  </div>
  <div class="space-y-5 rounded-lg border bg-card p-3 @md:p-4">
    {#each Array.from({ length: 2 }, (_, i) => i) as i (i)}
      <div class="space-y-2">
        <Skeleton class="h-4 w-32" />
        <Skeleton class="h-36 w-full" />
      </div>
    {/each}
  </div>
</div>
