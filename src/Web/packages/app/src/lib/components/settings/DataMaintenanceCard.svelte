<script lang="ts">
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import {
    Link2,
    Loader2,
    RefreshCw,
    Sparkles,
    Wrench,
    ChevronRight,
  } from "lucide-svelte";
  import DeduplicationDialog from "$lib/components/connectors/DeduplicationDialog.svelte";
  import DemoDataSection from "$lib/components/connectors/DemoDataSection.svelte";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";

  const isPlatformAdmin = $derived(
    (page.data as { isPlatformAdmin?: boolean }).isPlatformAdmin ?? false,
  );

  let showDeduplicationDialog = $state(false);
  let isDeduplicating = $state(false);
  let showDemoDataDialog = $state(false);
</script>

<Card>
  <CardHeader>
    <CardTitle class="flex items-center gap-2">
      <Wrench class="h-5 w-5" />
      Data Maintenance
    </CardTitle>
    <CardDescription>Administrative tools for managing your data</CardDescription>
  </CardHeader>
  <CardContent class="space-y-4">
    <div
      data-testid="deduplicate-records"
      class="flex items-start gap-4 p-4 rounded-lg border bg-card"
    >
      <div
        class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-primary/10"
      >
        <Link2 class="h-5 w-5 text-primary" />
      </div>
      <div class="flex-1">
        <h4 class="font-medium">Deduplicate Records</h4>
        <p class="text-sm text-muted-foreground mt-1">
          Link records from multiple data sources that represent the same
          underlying event. This improves data quality when the same glucose
          readings or treatments are uploaded from different apps.
        </p>
        <Button
          variant="outline"
          size="sm"
          class="mt-3 gap-2"
          onclick={() => (showDeduplicationDialog = true)}
        >
          {#if isDeduplicating}
            <Loader2 class="h-4 w-4 animate-spin" />
            Deduplication Running...
          {:else}
            <Link2 class="h-4 w-4" />
            Run Deduplication
          {/if}
        </Button>
      </div>
    </div>

    <div class="flex items-start gap-4 p-4 rounded-lg border bg-card">
      <div
        class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-primary/10"
      >
        <Sparkles class="h-5 w-5 text-primary" />
      </div>
      <div class="flex-1">
        <h4 class="font-medium">Remove Demo Data</h4>
        <p class="text-sm text-muted-foreground mt-1">
          Delete the sample readings and treatments that were generated to show
          you around. Your own data is not affected.
        </p>
        <Button
          variant="outline"
          size="sm"
          class="mt-3 gap-2"
          onclick={() => (showDemoDataDialog = true)}
        >
          <Sparkles class="h-4 w-4" />
          Remove Demo Data
        </Button>
      </div>
    </div>

    {#if isPlatformAdmin}
      <a
        href={resolve("/settings/admin/connector-cursors")}
        class="group flex items-center gap-4 rounded-lg border bg-card p-4 transition-colors hover:border-primary/40 hover:bg-muted/40"
      >
        <div
          class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-primary/10"
        >
          <RefreshCw class="h-5 w-5 text-primary" />
        </div>
        <div class="min-w-0 flex-1">
          <h4 class="font-medium">Reset Connector Cursors</h4>
          <p class="text-sm text-muted-foreground mt-1">
            Re-sync a connector from a chosen point.
          </p>
        </div>
        <ChevronRight
          class="h-4 w-4 shrink-0 text-muted-foreground transition-transform group-hover:translate-x-0.5"
        />
      </a>
    {/if}
  </CardContent>
</Card>

<DemoDataSection bind:open={showDemoDataDialog} />
<DeduplicationDialog bind:open={showDeduplicationDialog} bind:isDeduplicating />
