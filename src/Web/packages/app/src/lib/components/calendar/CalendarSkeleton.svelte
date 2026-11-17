<script lang="ts">
  import { Skeleton } from "#lib/components/ui/skeleton/index.js";
  import * as Card from "#lib/components/ui/card/index.js";
</script>

<div class="@container flex flex-col h-full">
  <div class="sticky top-(--app-sticky-top,0px) z-10 transition-all duration-300">
    <div class="@container border-b bg-background/95 backdrop-blur supports-backdrop-filter:bg-background/60">
      <div class="flex flex-wrap items-center justify-between gap-2 p-4">
        <div class="flex items-center gap-4">
          <Skeleton class="h-6 w-6 rounded" />
          <Skeleton class="h-8 w-28" />
        </div>

        <div class="flex flex-wrap items-center gap-2">
          <Skeleton class="h-9 w-9 rounded-md" />
          <Skeleton class="h-7 w-32 @md:w-44" />
          <Skeleton class="h-9 w-9 rounded-md" />
        </div>
      </div>

      <div class="flex flex-wrap items-center justify-between gap-x-4 gap-y-2 px-4 pb-3">
        <div class="flex flex-wrap items-center gap-3 @xl:gap-6">
          <Skeleton class="h-7 w-28 rounded-md" />
          <Skeleton class="h-6 w-28" />
          <div class="flex flex-wrap items-center gap-x-3 gap-y-1">
            {#each Array(5) as _, i (i)}
              <div class="flex items-center gap-1.5">
                <Skeleton class="h-3 w-3 rounded" />
                <Skeleton class="h-5 w-14" />
              </div>
            {/each}
          </div>
        </div>
        <Skeleton class="hidden h-5 w-48 @xl:block" />
      </div>
    </div>
  </div>

  <div class="flex-1 p-3 @md:p-4">
    <Card.Root class="h-full">
      <Card.Content class="p-2 @md:p-4 h-full flex flex-col">
        <div class="flex-1 overflow-x-auto flex flex-col">
          <div class="grid grid-cols-7 gap-1 mb-2 min-w-[28rem] @md:min-w-0">
            {#each Array(7) as _, i (i)}
              <div class="flex justify-center py-2">
                <Skeleton class="h-5 w-8" />
              </div>
            {/each}
          </div>

          <div class="flex-1 grid grid-rows-6 gap-1 min-w-[28rem] @md:min-w-0">
            {#each Array(6) as _, idx (idx)}
              <div class="grid grid-cols-7 gap-1">
                {#each Array(7) as _, i (i)}
                  <div
                    class="flex items-center justify-center rounded-lg border border-border/50 min-h-20 relative"
                  >
                    <Skeleton class="absolute top-1 left-2 h-3 w-4" />
                    <Skeleton class="h-10 w-10 rounded-full" />
                  </div>
                {/each}
              </div>
            {/each}
          </div>
        </div>

        <div class="@container mt-4 pt-4 border-t">
          <div class="grid grid-cols-2 @lg:grid-cols-4 gap-4">
            {#each Array(4) as _, i (i)}
              <div class="text-center">
                <Skeleton class="h-8 w-16 mx-auto mb-1" />
                <Skeleton class="h-4 w-20 mx-auto" />
              </div>
            {/each}
          </div>
        </div>
      </Card.Content>
    </Card.Root>
  </div>
</div>
