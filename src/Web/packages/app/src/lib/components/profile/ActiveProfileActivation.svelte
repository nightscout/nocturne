<script lang="ts">
  import {
    getActivationStatus,
    endActivation,
    renewActivation,
  } from "$api/generated/profileActivations.generated.remote";
  import { ProfileActivationOrigin, type ProfileActivationDto } from "$api-clients";
  import { Card, CardContent, CardHeader, CardTitle } from "$lib/components/ui/card";
  import * as Alert from "$lib/components/ui/alert";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import User from "@lucide/svelte/icons/user";
  import Smartphone from "@lucide/svelte/icons/smartphone";
  import Plug from "@lucide/svelte/icons/plug";
  import Repeat from "@lucide/svelte/icons/repeat";
  import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
  import { profileActivationSource } from "$lib/utils/profile-activation-label";
  import { formatDateTimeCompact } from "$lib/utils/formatting";
  import { remoteErrorMessage } from "$lib/api/remote-error";
  import { describeSubmitError } from "$lib/forms";

  const statusQuery = getActivationStatus(undefined);
  const status = $derived(statusQuery.current);
  const active = $derived(status?.active);
  const recorded = $derived(status?.recorded);
  const ignored = $derived(status?.recordedSwitchesApply === false ? recorded : undefined);

  let busy = $state(false);
  let actionError = $state<string | null>(null);

  // Refetch when a shown activation reaches its end, so a lapsed switch does not stay on screen.
  $effect(() => {
    const ends = [active?.endMills, recorded?.endMills].filter(
      (end): end is number => end != null
    );
    const timers = ends.map((end) =>
      setTimeout(() => statusQuery.refresh(), Math.max(0, end - Date.now()) + 1000)
    );
    return () => timers.forEach(clearTimeout);
  });

  async function run(action: () => Promise<unknown>, fallback: string) {
    busy = true;
    actionError = null;
    try {
      await action();
    } catch (err) {
      actionError = describeSubmitError(err, fallback);
    } finally {
      busy = false;
    }
  }
</script>

{#snippet badges(activation: ProfileActivationDto)}
  <span class="font-medium">{activation.baseProfileName}</span>
  {#if activation.percentage != null && activation.percentage !== 100}
    <Badge variant="secondary">{activation.percentage}%</Badge>
  {/if}
  {#if activation.timeshiftHours}
    <Badge variant="secondary">
      {activation.timeshiftHours > 0 ? "+" : ""}{activation.timeshiftHours}h
    </Badge>
  {/if}
{/snippet}

{#snippet provenance(activation: ProfileActivationDto)}
  <p class="text-xs text-muted-foreground flex flex-wrap items-center gap-1">
    {#if activation.origin === ProfileActivationOrigin.User}
      <User class="h-3 w-3" />
      Recorded by {activation.enteredBy ?? "a person"}
    {:else if activation.origin === ProfileActivationOrigin.Connector}
      <Plug class="h-3 w-3" />
      Reported by {profileActivationSource(activation) ?? "a connector"}
    {:else}
      <Smartphone class="h-3 w-3" />
      Uploaded by {profileActivationSource(activation) ?? activation.enteredBy ?? "an uploader"}
    {/if}
    &middot; Since {formatDateTimeCompact(activation.startMills)}
    {#if activation.endMills != null}
      &middot; Until {formatDateTimeCompact(activation.endMills)}
    {/if}
  </p>
  {#if activation.notes}
    <p class="text-xs text-muted-foreground">{activation.notes}</p>
  {/if}
{/snippet}

<Card>
  <CardHeader class="pb-3">
    <CardTitle class="text-lg flex items-center gap-2">
      <Repeat class="h-5 w-5" />
      Active profile switch
    </CardTitle>
  </CardHeader>
  <CardContent class="space-y-3">
    {#if statusQuery.error}
      <p class="text-sm text-destructive">
        {remoteErrorMessage(statusQuery.error, "The active switch could not be loaded.")}
      </p>
    {:else if status}
      {#if active}
        <div class="space-y-1">
          <div class="flex flex-wrap items-center gap-2">
            {@render badges(active)}
          </div>
          {@render provenance(active)}
        </div>
      {:else}
        <p class="text-sm text-muted-foreground">
          No switch is active. The default profile applies.
        </p>
      {/if}

      {#if ignored}
        <Alert.Root variant="warning">
          <TriangleAlert class="h-4 w-4" />
          <Alert.Title>
            <span class="flex flex-wrap items-center gap-2">{@render badges(ignored)}</span>
          </Alert.Title>
          <Alert.Description>
            This switch is recorded, but Nocturne's calculations follow the profile
            {profileActivationSource(status.reported) ?? "your AID system"} reports while it does.
          </Alert.Description>
        </Alert.Root>
      {/if}

      {#if recorded?.id}
        {@const id = recorded.id}
        {#if status.recordedProfileChanged}
          <p class="text-sm text-muted-foreground">
            {recorded.baseProfileName} has changed since this switch was recorded. The switch keeps
            the schedules it was recorded with.
          </p>
        {/if}
        <div class="flex flex-wrap gap-2">
          {#if status.recordedProfileChanged}
            <Button
              size="sm"
              disabled={busy}
              onclick={() => run(() => renewActivation(id), "The switch was not recorded again.")}
            >
              Switch again
            </Button>
          {/if}
          <Button
            variant="outline"
            size="sm"
            disabled={busy}
            onclick={() => run(() => endActivation(id), "The switch was not ended.")}
          >
            End switch
          </Button>
        </div>
      {/if}

      {#if actionError}
        <p class="text-sm text-destructive" role="alert">{actionError}</p>
      {/if}
    {/if}
  </CardContent>
</Card>
