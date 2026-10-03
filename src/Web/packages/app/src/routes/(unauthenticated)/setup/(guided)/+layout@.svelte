<script lang="ts">
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import ArrowLeft from "@lucide/svelte/icons/arrow-left";
  import Check from "@lucide/svelte/icons/check";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import * as Card from "$lib/components/ui/card";
  import { FormError } from "$lib/forms";
  import { describeSubmitError } from "$lib/forms/submit-error";
  import {
    getSetupHub,
    setSetupHubItemState,
  } from "$api/generated/setupHubs.generated.remote";
  import { getPatientRelationship } from "$api/generated/tenantSettings.generated.remote";
  import { SetupHubItemState, type SetupHubItemKey } from "$api";
  import { patientVoice } from "$lib/onboarding/patient-voice.svelte";
  import { setupHubItems } from "$lib/setup-hub/items.svelte";
  import SetupChrome from "../SetupChrome.svelte";

  let { children } = $props();

  const key = $derived<SetupHubItemKey>(page.data.item);
  const view = $derived(setupHubItems()[key]);

  const hubQuery = getSetupHub();
  const relationshipQuery = getPatientRelationship();
  const voice = $derived(patientVoice(relationshipQuery.current));
  const itemState = $derived(hubQuery.current?.items?.find((i) => i.key === key)?.state);

  let error = $state<string | undefined>(undefined);

  async function setAside(next: SetupHubItemState) {
    try {
      error = undefined;
      await setSetupHubItemState({ key, request: { state: next } });
      if (next === SetupHubItemState.NotForMe) await goto(resolve("/setup"));
    } catch (err) {
      error = describeSubmitError(err, "We couldn't save that.");
    }
  }
</script>

<svelte:head>
  <title>{view.title} - Setup - Nocturne</title>
</svelte:head>

<SetupChrome>
  {#snippet actions()}
    <Button variant="ghost-muted" size="xs" href={resolve("/setup")}>
      <ArrowLeft class="h-4 w-4" />
      Back to setup
    </Button>
  {/snippet}

  <Card.Root size="flush" class="mx-auto w-full max-w-3xl" data-testid="guided-item">
    <div class="flex items-start gap-4 border-b px-7 py-6">
      <span class="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-primary/10">
        <view.icon class="h-5 w-5 text-primary" />
      </span>
      <div class="flex flex-col gap-1">
        <h1 class="font-brand text-3xl font-light leading-tight">{view.title}</h1>
        <p class="text-sm text-muted-foreground">{view.description(voice)}</p>
      </div>
    </div>

    <div class="px-7 py-6">
      {@render children()}
    </div>

    <div class="flex flex-wrap items-center justify-between gap-3 border-t bg-muted/40 px-7 py-4">
      <Button variant="outline" href={resolve("/setup")}>
        <ArrowLeft class="h-4 w-4" />
        Back to setup
      </Button>
      <div class="flex items-center gap-3">
        <FormError issues={error} />
        {#if itemState === SetupHubItemState.Done}
          <Badge variant="success"><Check />Done</Badge>
        {:else if itemState === SetupHubItemState.NotForMe}
          <Button variant="ghost" onclick={() => setAside(SetupHubItemState.Open)}>
            Put it back on the list
          </Button>
        {:else if itemState === SetupHubItemState.Open}
          <Button variant="ghost" onclick={() => setAside(SetupHubItemState.NotForMe)}>
            {view.notForMeLabel ?? "Not for me"}
          </Button>
        {/if}
      </div>
    </div>
  </Card.Root>
</SetupChrome>
