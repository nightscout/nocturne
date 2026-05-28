<script lang="ts">
  import { Skeleton } from "$lib/components/ui/skeleton";
  import { Card, CardContent, CardHeader } from "$lib/components/ui/card";
</script>

<div class="@container container mx-auto space-y-6 p-3 @md:p-6 @lg:space-y-8">
  <!-- Header Skeleton -->
  <div class="space-y-3 text-center">
    <div class="flex items-center justify-center gap-2">
      <Skeleton class="h-4 w-4 rounded" />
      <Skeleton class="h-4 w-40" />
      <Skeleton class="h-4 w-24" />
    </div>
    <Skeleton class="h-10 w-72 mx-auto" />
    <Skeleton class="h-6 w-96 max-w-full mx-auto" />
  </div>

  <!-- Quick Actions Skeleton -->
  <div class="flex flex-wrap items-center justify-center gap-2">
    <Skeleton class="h-9 w-40 rounded-md" />
    <Skeleton class="h-9 w-32 rounded-md" />
    <Skeleton class="h-9 w-28 rounded-md" />
  </div>

  <!-- Score Cards Skeleton -->
  <div class="grid grid-cols-1 gap-4 @xl:grid-cols-2 @5xl:grid-cols-4">
    {#each Array(4) as _}
      <Card>
        <CardHeader class="pb-2">
          <div class="flex items-center gap-2">
            <Skeleton class="h-5 w-5 rounded" />
            <Skeleton class="h-4 w-24" />
          </div>
        </CardHeader>
        <CardContent>
          <Skeleton class="h-12 w-20 mb-2" />
          <Skeleton class="h-3 w-full" />
          <Skeleton class="h-3 w-3/4 mt-1" />
        </CardContent>
      </Card>
    {/each}
  </div>

  <!-- Charts Row Skeleton -->
  <div class="grid grid-cols-1 gap-6 @4xl:grid-cols-3">
    <Card class="@4xl:col-span-1">
      <CardHeader>
        <div class="flex items-center gap-2">
          <Skeleton class="h-5 w-5 rounded" />
          <Skeleton class="h-5 w-28" />
        </div>
        <Skeleton class="h-4 w-40" />
      </CardHeader>
      <CardContent class="flex justify-center">
        <Skeleton class="h-48 w-48 rounded-full" />
      </CardContent>
    </Card>

    <Card class="@4xl:col-span-2">
      <CardHeader>
        <div class="flex items-center justify-between">
          <div>
            <div class="flex items-center gap-2">
              <Skeleton class="h-5 w-5 rounded" />
              <Skeleton class="h-5 w-32" />
            </div>
            <Skeleton class="h-4 w-56 mt-1" />
          </div>
          <Skeleton class="h-9 w-28 rounded-md" />
        </div>
      </CardHeader>
      <CardContent class="h-64">
        <Skeleton class="h-full w-full rounded-lg" />
      </CardContent>
    </Card>
  </div>

  <!-- Stats Grid Skeleton -->
  <div class="grid grid-cols-2 gap-3 @sm:grid-cols-3 @3xl:grid-cols-6">
    {#each Array(6) as _}
      <Card class="p-4 text-center">
        <Skeleton class="h-8 w-16 mx-auto mb-2" />
        <Skeleton class="h-3 w-12 mx-auto" />
      </Card>
    {/each}
  </div>
</div>
