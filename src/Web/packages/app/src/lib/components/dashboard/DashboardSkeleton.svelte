<script lang="ts">
  import { Skeleton } from "$lib/components/ui/skeleton";
  import { Card, CardContent, CardHeader } from "$lib/components/ui/card";
</script>

<div class="container mx-auto p-6 space-y-6">
  <!-- Header Skeleton -->
  <div class="flex items-center justify-between">
    <div class="space-y-2">
      <Skeleton class="h-9 w-64" />
      <Skeleton class="h-4 w-96" />
    </div>
    <div class="flex items-center gap-2">
      <Skeleton class="h-9 w-24 rounded-md" />
      <Skeleton class="h-9 w-24 rounded-md" />
    </div>
  </div>

  <!-- Overall Status Cards Skeleton (4-card grid) -->
  <div class="grid gap-4 md:grid-cols-2 lg:grid-cols-4">
    {#each Array(4) as _, i (i)}
      <Card>
        <CardHeader
          class="flex flex-row items-center justify-between space-y-0 pb-2"
        >
          <Skeleton class="h-4 w-28" />
          <Skeleton class="h-4 w-4 rounded" />
        </CardHeader>
        <CardContent>
          <Skeleton class="h-8 w-20 mb-2" />
          <Skeleton class="h-4 w-24" />
        </CardContent>
      </Card>
    {/each}
  </div>

  <!-- Detailed Metrics Skeleton (2-column grid) -->
  <div class="grid gap-6 md:grid-cols-2">
    <!-- Left Card -->
    <Card>
      <CardHeader>
        <Skeleton class="h-5 w-48 mb-2" />
        <Skeleton class="h-4 w-64" />
      </CardHeader>
      <CardContent class="space-y-4">
        {#each Array(4) as _, i (i)}
          <div class="flex justify-between items-center">
            <Skeleton class="h-4 w-28" />
            <Skeleton class="h-6 w-20 rounded-full" />
          </div>
        {/each}
      </CardContent>
    </Card>

    <!-- Right Card -->
    <Card>
      <CardHeader>
        <Skeleton class="h-5 w-44 mb-2" />
        <Skeleton class="h-4 w-56" />
      </CardHeader>
      <CardContent class="space-y-3">
        {#each Array(5) as _, i (i)}
          <div class="flex items-center justify-between p-2 rounded-lg border">
            <div class="flex-1 space-y-2">
              <Skeleton class="h-4 w-40" />
              <Skeleton class="h-3 w-32" />
            </div>
            <div class="flex gap-1">
              <Skeleton class="h-5 w-8 rounded-full" />
              <Skeleton class="h-5 w-8 rounded-full" />
            </div>
          </div>
        {/each}
        <div class="pt-4 border-t">
          <Skeleton class="h-9 w-full rounded-md" />
        </div>
      </CardContent>
    </Card>
  </div>

  <!-- Recent Analyses Skeleton -->
  <Card>
    <CardHeader>
      <Skeleton class="h-5 w-56 mb-2" />
      <Skeleton class="h-4 w-48" />
    </CardHeader>
    <CardContent class="space-y-3">
      {#each Array(5) as _, i (i)}
        <div class="flex items-center justify-between p-3 rounded-lg border">
          <div class="flex-1 space-y-2">
            <div class="flex items-center gap-2">
              <Skeleton class="h-5 w-12 rounded" />
              <Skeleton class="h-4 w-48" />
              <Skeleton class="h-5 w-24 rounded-full" />
            </div>
            <Skeleton class="h-3 w-64" />
          </div>
          <div class="flex items-center gap-2">
            <Skeleton class="h-5 w-8 rounded-full" />
            <Skeleton class="h-8 w-8 rounded-md" />
          </div>
        </div>
      {/each}
      <div class="pt-4 border-t">
        <Skeleton class="h-9 w-full rounded-md" />
      </div>
    </CardContent>
  </Card>
</div>
