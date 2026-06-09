<script lang="ts">
  import { Skeleton } from "$lib/components/ui/skeleton";
  import { Card, CardContent, CardHeader } from "$lib/components/ui/card";

  interface Props {
    /** Number of card sections to show */
    cardCount?: number;
  }

  let { cardCount = 3 }: Props = $props();
</script>

<div class="container mx-auto p-6 max-w-3xl space-y-6">
  <!-- Header Skeleton -->
  <div class="space-y-2">
    <Skeleton class="h-8 w-48" />
    <Skeleton class="h-4 w-72" />
  </div>

  <!-- Card Skeletons -->
  {#each Array(cardCount) as _, i}
    <Card>
      <CardHeader>
        <div class="flex items-center gap-3">
          <Skeleton class="h-10 w-10 rounded-lg" />
          <div class="space-y-2 flex-1">
            <Skeleton class="h-5 w-40" />
            <Skeleton class="h-4 w-64" />
          </div>
        </div>
      </CardHeader>
      <CardContent class="space-y-4 @container">
        <!-- Vary the content based on card index for visual variety -->
        {#if i % 3 === 0}
          <!-- Toggle rows -->
          <div class="flex items-center justify-between">
            <div class="space-y-2">
              <Skeleton class="h-4 w-32" />
              <Skeleton class="h-3 w-48" />
            </div>
            <Skeleton class="h-6 w-11 rounded-full" />
          </div>
          <div class="flex items-center justify-between">
            <div class="space-y-2">
              <Skeleton class="h-4 w-28" />
              <Skeleton class="h-3 w-44" />
            </div>
            <Skeleton class="h-6 w-11 rounded-full" />
          </div>
        {:else if i % 3 === 1}
          <!-- Grid of inputs -->
          <div class="grid gap-4 @sm:grid-cols-2">
            <div class="space-y-2">
              <Skeleton class="h-4 w-24" />
              <Skeleton class="h-10 w-full rounded-md" />
            </div>
            <div class="space-y-2">
              <Skeleton class="h-4 w-28" />
              <Skeleton class="h-10 w-full rounded-md" />
            </div>
          </div>
          <Skeleton class="h-4 w-full max-w-md" />
        {:else}
          <!-- List items -->
          <div class="space-y-3">
            {#each Array(3) as _}
              <div class="flex items-center gap-4 p-3 rounded-lg border">
                <Skeleton class="h-10 w-10 rounded-lg" />
                <div class="flex-1 space-y-2">
                  <Skeleton class="h-4 w-32" />
                  <Skeleton class="h-3 w-48" />
                </div>
                <Skeleton class="h-8 w-8 rounded-md" />
              </div>
            {/each}
          </div>
        {/if}
      </CardContent>
    </Card>
  {/each}
</div>
