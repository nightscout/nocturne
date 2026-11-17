<script lang="ts">
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "#lib/components/ui/card/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { foldStoredSpans } from "#lib/api/generated/stateSpanMaintenances.generated.remote.js";
  import type { StateSpanFoldResult } from "#lib/api/generated/nocturne-api-client.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import Layers from "@lucide/svelte/icons/layers";
  import Link2 from "@lucide/svelte/icons/link-2";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import Sparkles from "@lucide/svelte/icons/sparkles";
  import Wrench from "@lucide/svelte/icons/wrench";
  import DeduplicationDialog from "#lib/components/connectors/DeduplicationDialog.svelte";
  import DemoDataSection from "#lib/components/connectors/DemoDataSection.svelte";
  import { page } from "$app/state";
  import { satisfiesScope } from "#lib/authorization/scopes.js";
  import { canManageConnectors } from "#lib/authorization/connector-management.js";

  // The two tools answer to different gates: deduplication is RequireSuperuser, so it resolves to
  // the wildcard; deleting demo data is tenant.settings and refuses the demo visitor. Data
  // Quality is a page an ordinary member opens to set a sleep schedule, so neither control is
  // offered to someone the API would refuse.
  const effectivePermissions = $derived(page.data.effectivePermissions ?? []);

  const canDeduplicate = $derived(satisfiesScope(effectivePermissions, "*"));
  const canManageData = $derived(canManageConnectors(effectivePermissions, page.data.refusedAsDemoSubject));
  let showDeduplicationDialog = $state(false);
  let isDeduplicating = $state(false);

  // Fold contiguous pump-mode spans: a pump that reports its mode as a stream of short readings
  // leaves many same-state spans that the write path now merges; this applies the rule to what
  // is already stored.
  let isFolding = $state(false);
  let foldResult = $state<StateSpanFoldResult | null>(null);
  let foldError = $state<string | null>(null);

  async function handleFoldSpans() {
    isFolding = true;
    foldError = null;
    foldResult = null;
    try {
      foldResult = await foldStoredSpans();
    } catch (e) {
      foldError = describeSubmitError(e, "Could not fold state spans.");
    } finally {
      isFolding = false;
    }
  }
  let showDemoDataDialog = $state(false);
</script>

{#if canDeduplicate || canManageData}
  <Card>
    <CardHeader>
      <CardTitle class="flex items-center gap-2">
        <Wrench class="h-5 w-5" />
        Data maintenance
      </CardTitle>
      <CardDescription>
        Administrative tools for managing your data
      </CardDescription>
    </CardHeader>
    <CardContent class="space-y-4">
      {#if canDeduplicate}
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
            <h4 class="font-medium">Deduplicate records</h4>
            <p class="text-sm text-muted-foreground mt-1">
              Link records from multiple data sources that represent the same
              underlying event. This improves data quality when the same glucose
              readings or treatments are uploaded from different apps.
            </p>
            <Button
              variant="outline"
              size="sm"
              class="mt-3"
              onclick={() => showDeduplicationDialog = true}
            >
              {#if isDeduplicating}
                <Loader2 class="h-4 w-4 animate-spin" />
                Deduplication running...
              {:else}
                <Link2 class="h-4 w-4" />
                Run deduplication
              {/if}
            </Button>
          </div>
        </div>
      {/if}

      {#if canDeduplicate}
        <div
          data-testid="fold-state-spans"
          class="flex items-start gap-4 p-4 rounded-lg border bg-card"
        >
          <div
            class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-primary/10"
          >
            <Layers class="h-5 w-5 text-primary" />
          </div>
          <div class="flex-1">
            <h4 class="font-medium">Fold contiguous pump modes</h4>
            <p class="text-sm text-muted-foreground mt-1">
              Merge pump-mode and connectivity spans that touch or sit within a minute of each
              other into one span per continuous state. New imports are folded as they arrive; this
              applies the same rule to spans already stored.
            </p>
            <Button
              variant="outline"
              size="sm"
              class="mt-3"
              onclick={handleFoldSpans}
              disabled={isFolding}
            >
              {#if isFolding}
                <Loader2 class="h-4 w-4 animate-spin" />
                Folding...
              {:else}
                <Layers class="h-4 w-4" />
                Fold spans
              {/if}
            </Button>
            {#if foldResult}
              <p class="text-sm text-muted-foreground mt-2">
                {foldResult.examined} spans examined, {foldResult.widened} merges, {foldResult.removed} spans absorbed.
              </p>
            {/if}
            {#if foldError}
              <p class="text-sm text-destructive mt-2">{foldError}</p>
            {/if}
          </div>
        </div>
      {/if}

      {#if canManageData}
        <div class="flex items-start gap-4 p-4 rounded-lg border bg-card">
          <div
            class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-primary/10"
          >
            <Sparkles class="h-5 w-5 text-primary" />
          </div>
          <div class="flex-1">
            <h4 class="font-medium">Remove demo data</h4>
            <p class="text-sm text-muted-foreground mt-1">
              Delete the sample readings and treatments that were generated to
              show you around. Your own data is not affected.
            </p>
            <Button
              variant="outline"
              size="sm"
              class="mt-3"
              onclick={() => showDemoDataDialog = true}
            ><Sparkles class="h-4 w-4" />Remove demo data</Button>
          </div>
        </div>
      {/if}

    </CardContent>
  </Card>

  <DemoDataSection bind:open={showDemoDataDialog} />
  <DeduplicationDialog
    bind:open={showDeduplicationDialog}
    bind:isDeduplicating
  />
{/if}
