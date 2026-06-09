<script lang="ts">
  import { Skeleton } from "$lib/components/ui/skeleton";
  import * as Card from "$lib/components/ui/card";
</script>

<div class="flex flex-col h-full">
  <!-- Header Skeleton -->
  <div class="border-b bg-background/95 backdrop-blur sticky top-0 z-10">
    <div class="flex items-center justify-between p-4">
      <div class="flex items-center gap-4">
        <Skeleton class="h-6 w-6 rounded" />
        <Skeleton class="h-8 w-28" />
      </div>

      <!-- Navigation Controls Skeleton -->
      <div class="flex items-center gap-2">
        <Skeleton class="h-9 w-9 rounded-md" />
        <Skeleton class="h-6 w-44" />
        <Skeleton class="h-9 w-9 rounded-md" />
        <Skeleton class="h-9 w-16 rounded-md" />
      </div>

      <!-- Size Mode Selector Skeleton -->
      <div class="flex items-center gap-2">
        <Skeleton class="h-4 w-16" />
        <Skeleton class="h-9 w-44 rounded-md" />
      </div>
    </div>

    <!-- Legend Skeleton -->
    <div class="flex items-center justify-between px-4 pb-3">
      <div class="flex items-center gap-4">
        {#each Array(3) as _}
          <div class="flex items-center gap-2">
            <Skeleton class="h-3 w-3 rounded-full" />
            <Skeleton class="h-4 w-16" />
          </div>
        {/each}
      </div>
      <Skeleton class="h-4 w-48" />
    </div>
  </div>

  <!-- Calendar Grid Skeleton -->
  <div class="flex-1 p-4">
    <Card.Root class="h-full">
      <Card.Content class="@container p-4 h-full flex flex-col">
        <!-- Day of week headers -->
        <div class="grid grid-cols-7 gap-1 mb-2">
          {#each Array(7) as _}
            <div class="text-center py-2">
              <Skeleton class="h-4 w-8 mx-auto" />
            </div>
          {/each}
        </div>

        <!-- Calendar grid -->
        <div class="flex-1 grid grid-rows-5 gap-1">
          {#each Array(5) as _}
            <div class="grid grid-cols-7 gap-1">
              {#each Array(7) as _}
                <div
                  class="flex items-center justify-center rounded-lg border border-border/50 min-h-20 relative p-2"
                >
                  <Skeleton class="absolute top-1 left-2 h-3 w-4" />
                  <Skeleton class="h-10 w-10 rounded-full" />
                </div>
              {/each}
            </div>
          {/each}
        </div>

        <!-- Month Summary Skeleton -->
        <div class="mt-4 pt-4 border-t grid grid-cols-2 @lg:grid-cols-4 gap-4">
          {#each Array(4) as _}
            <div class="text-center">
              <Skeleton class="h-8 w-16 mx-auto mb-1" />
              <Skeleton class="h-4 w-20 mx-auto" />
            </div>
          {/each}
        </div>
      </Card.Content>
    </Card.Root>
  </div>
</div>
