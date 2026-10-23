<script lang="ts">
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import Check from "@lucide/svelte/icons/check";
  import { Button } from "$lib/components/ui/button";
  import { FormError } from "$lib/forms";
  import { describeSubmitError } from "$lib/forms/submit-error";
  import CreateInviteCard from "$lib/components/members/CreateInviteCard.svelte";
  import GuestLinksSection from "$lib/components/members/GuestLinksSection.svelte";
  import PublicAccessCard from "$lib/components/members/PublicAccessCard.svelte";
  import { getRoles } from "$api/generated/roles.generated.remote";
  import {
    getSetupHub,
    setSetupHubItemState,
  } from "$api/generated/setupHubs.generated.remote";
  import { getPatientRelationship } from "$api/generated/tenantSettings.generated.remote";
  import { SetupHubItemKey, SetupHubItemState } from "$api";
  import { patientVoice } from "$lib/onboarding/patient-voice.svelte";
  import {
    familyRoleChoices,
    sharingAudiences,
    sharingQuestion,
    type SharingAudience,
    type SharingAudienceId,
  } from "./audiences.svelte";

  const hubQuery = getSetupHub();
  const relationshipQuery = getPatientRelationship();
  const rolesQuery = getRoles();

  const voice = $derived(patientVoice(relationshipQuery.current));
  const audiences = $derived(sharingAudiences(voice));
  const roleChoices = $derived(familyRoleChoices(rolesQuery.current ?? []));
  const itemState = $derived(
    hubQuery.current?.items?.find((i) => i.key === SetupHubItemKey.Sharing)?.state,
  );

  let chosen = $state<SharingAudienceId[]>([]);
  let error = $state<string | undefined>(undefined);
  let keeping = $state(false);

  function toggle(audience: SharingAudience) {
    if (chosen.includes(audience.id)) {
      chosen = chosen.filter((id) => id !== audience.id);
      return;
    }
    const exclusiveIds = audiences.filter((a) => a.exclusive).map((a) => a.id);
    chosen = audience.exclusive
      ? [audience.id]
      : [...chosen.filter((id) => !exclusiveIds.includes(id)), audience.id];
  }

  const close = (id: SharingAudienceId) => (chosen = chosen.filter((c) => c !== id));

  /** The item is done by what was just created, which only a fresh read of the hub reports. */
  const shared = () => void hubQuery.refresh();

  async function keepItToMe() {
    keeping = true;
    error = undefined;
    try {
      if (itemState !== SetupHubItemState.Done && itemState !== SetupHubItemState.NotForMe) {
        await setSetupHubItemState({
          key: SetupHubItemKey.Sharing,
          request: { state: SetupHubItemState.NotForMe },
        });
      }
      await goto(resolve("/setup"));
    } catch (err) {
      error = describeSubmitError(err, "We couldn't save that.");
    } finally {
      keeping = false;
    }
  }
</script>

<div class="flex flex-col gap-6">
  <div class="flex flex-col gap-1">
    <h2 class="text-lg font-semibold" id="sharing-question">{sharingQuestion(voice)}</h2>
    <p class="text-sm text-muted-foreground">
      Choose as many as you like. Each one you choose opens below.
    </p>
  </div>

  <div class="grid gap-3 sm:grid-cols-2" role="group" aria-labelledby="sharing-question">
    {#each audiences as audience (audience.id)}
      {@const on = chosen.includes(audience.id)}
      <button
        type="button"
        aria-pressed={on}
        data-testid="sharing-audience-{audience.id}"
        class="relative flex items-start gap-3 rounded-lg border-2 p-4 text-left outline-none transition-colors hover:bg-accent/50 focus-visible:border-ring focus-visible:ring-2 focus-visible:ring-ring/50 {on
          ? 'border-primary bg-accent/30'
          : 'border-border'}"
        onclick={() => toggle(audience)}
      >
        <span
          class="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg {on
            ? 'bg-primary/15 text-primary'
            : 'bg-muted text-muted-foreground'}"
        >
          <audience.icon class="h-4 w-4" />
        </span>
        <span class="flex min-w-0 flex-1 flex-col gap-1 pr-6">
          <span class="text-sm font-medium">{audience.title}</span>
          <span class="text-xs leading-relaxed text-muted-foreground">{audience.description}</span>
        </span>
        {#if on}
          <Check class="absolute right-3 top-3 h-4 w-4 text-primary" />
        {/if}
      </button>
    {/each}
  </div>

  {#each audiences.filter((a) => chosen.includes(a.id)) as audience (audience.id)}
    <section class="flex flex-col gap-3" data-testid="sharing-flow-{audience.id}">
      {#if audience.id === "family"}
        <CreateInviteCard
          roles={rolesQuery.current ?? []}
          {roleChoices}
          onCreated={shared}
          onCancel={() => close("family")}
        />
      {:else if audience.id === "temporary"}
        <GuestLinksSection onCreated={shared} />
      {:else if audience.id === "public"}
        <p class="text-sm leading-relaxed text-muted-foreground">
          The link stays off until you turn it on. Once it is on, the card spells out exactly what
          the link shows, and it shows only the last 24 hours unless you choose all history. Anyone
          who gets hold of the link can open it, so send it only to people you trust.
        </p>
        <PublicAccessCard onEnabled={shared} />
      {:else if audience.id === "just-me"}
        <div class="flex flex-wrap items-center justify-between gap-3 rounded-lg border bg-muted/30 p-4">
          <p class="text-sm text-muted-foreground">
            Nothing is shared until you choose to. You can share any time from Settings.
          </p>
          <div class="flex items-center gap-3">
            <FormError issues={error} />
            <Button disabled={keeping} onclick={keepItToMe} data-testid="sharing-keep-to-me">
              Keep it to just me
            </Button>
          </div>
        </div>
      {/if}
    </section>
  {/each}
</div>
