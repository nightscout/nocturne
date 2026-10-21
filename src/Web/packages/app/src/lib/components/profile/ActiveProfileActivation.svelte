<script lang="ts">
  import {
    getActiveActivation,
    endActivation,
  } from "$api/generated/profileActivations.generated.remote";
  import { ProfileActivationOrigin } from "$api-clients";
  import { Card, CardContent, CardHeader, CardTitle } from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import User from "@lucide/svelte/icons/user";
  import Smartphone from "@lucide/svelte/icons/smartphone";
  import Plug from "@lucide/svelte/icons/plug";
  import Repeat from "@lucide/svelte/icons/repeat";
  import { getDataSourceDisplayName } from "$lib/utils/data-source-display";
  import { formatDateTimeCompact } from "$lib/utils/formatting";
  import { remoteErrorMessage } from "$lib/api/remote-error";
  import { describeSubmitError } from "$lib/forms";

  const activeQuery = getActiveActivation(undefined);
  const active = $derived(activeQuery.current);

  let ending = $state(false);
  let endError = $state<string | null>(null);

  const source = $derived(
    active ? (getDataSourceDisplayName(active.dataSource) ?? active.app ?? null) : null
  );

  async function end(id: string) {
    ending = true;
    endError = null;
    try {
      await endActivation(id);
    } catch (err) {
      endError = describeSubmitError(err, "The switch was not ended.");
    } finally {
      ending = false;
    }
  }
</script>

<Card>
  <CardHeader class="pb-3">
    <CardTitle class="text-lg flex items-center gap-2">
      <Repeat class="h-5 w-5" />
      Active profile switch
    </CardTitle>
  </CardHeader>
  <CardContent>
    {#if activeQuery.error}
      <p class="text-sm text-destructive">
        {remoteErrorMessage(activeQuery.error, "The active switch could not be loaded.")}
      </p>
    {:else if active}
      <div class="flex flex-wrap items-center justify-between gap-3">
        <div class="space-y-1">
          <div class="flex flex-wrap items-center gap-2">
            <span class="font-medium">{active.baseProfileName}</span>
            {#if active.percentage != null && active.percentage !== 100}
              <Badge variant="secondary">{active.percentage}%</Badge>
            {/if}
            {#if active.timeshiftHours}
              <Badge variant="secondary">
                {active.timeshiftHours > 0 ? "+" : ""}{active.timeshiftHours}h
              </Badge>
            {/if}
          </div>
          <p class="text-xs text-muted-foreground flex items-center gap-1">
            {#if active.origin === ProfileActivationOrigin.User}
              <User class="h-3 w-3" />
              Recorded by a person{source ? ` (${source})` : ""}
            {:else if active.origin === ProfileActivationOrigin.Connector}
              <Plug class="h-3 w-3" />
              Reported by {source ?? "a connector"}
            {:else}
              <Smartphone class="h-3 w-3" />
              Uploaded by {source ?? "an uploader"}
            {/if}
            &middot; Since {formatDateTimeCompact(active.startMills)}
            {#if active.endMills != null}
              &middot; Until {formatDateTimeCompact(active.endMills)}
            {/if}
          </p>
          {#if active.notes}
            <p class="text-xs text-muted-foreground">{active.notes}</p>
          {/if}
        </div>
        {#if active.origin === ProfileActivationOrigin.User && active.id}
          {@const id = active.id}
          <Button variant="outline" size="sm" disabled={ending} onclick={() => end(id)}>
            End switch
          </Button>
        {/if}
      </div>
      {#if endError}
        <p class="mt-2 text-sm text-destructive" role="alert">{endError}</p>
      {/if}
    {:else if !activeQuery.loading}
      <p class="text-sm text-muted-foreground">
        No switch is active. The default profile applies.
      </p>
    {/if}
  </CardContent>
</Card>
