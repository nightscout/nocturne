<script lang="ts">
  import {
    Card,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Luggage } from "lucide-svelte";
</script>

<div class="@container container mx-auto p-6 max-w-5xl space-y-6">
  <div>
    <h1 class="text-2xl font-bold tracking-tight">Tools</h1>
    <p class="text-muted-foreground">Utilities to help manage your diabetes.</p>
  </div>

  <div class="grid gap-4 @xl:grid-cols-2">
    <a href="/tools/packing" class="block">
      <Card class="h-full hover:bg-muted/50 transition-colors cursor-pointer">
        <CardHeader>
          <div class="flex items-center gap-3">
            <div class="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10">
              <Luggage class="h-5 w-5 text-primary" />
            </div>
            <div>
              <CardTitle class="text-base">Packing Calculator</CardTitle>
              <CardDescription class="text-xs">
                Calculate supplies needed for a trip
              </CardDescription>
            </div>
          </div>
        </CardHeader>
      </Card>
    </a>
  </div>
</div>
