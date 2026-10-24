<script lang="ts">
  import { resolve } from "$app/paths";
  import ArrowRight from "@lucide/svelte/icons/arrow-right";
  import CircleCheck from "@lucide/svelte/icons/circle-check";
  import { Button } from "$lib/components/ui/button";
  import * as Card from "$lib/components/ui/card";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import { getSetupHub } from "$api/generated/setupHubs.generated.remote";
  import { getPatientRelationship } from "$api/generated/tenantSettings.generated.remote";
  import { patientVoice } from "$lib/onboarding/patient-voice.svelte";
  import SetupChrome from "../SetupChrome.svelte";
  import HubItemRow from "./HubItemRow.svelte";
  import HubBasics from "./HubBasics.svelte";

  const hubQuery = getSetupHub();
  const relationshipQuery = getPatientRelationship();

  const hub = $derived(hubQuery.current);
  const voice = $derived(patientVoice(relationshipQuery.current));
  const complete = $derived(hub !== undefined && hub.openCount === 0);
</script>

<svelte:head>
  <title>Setup - Nocturne</title>
</svelte:head>

<SetupChrome>
  {#snippet actions()}
    <Button variant="ghost-muted" size="xs" href={resolve("/")}>Go to dashboard</Button>
  {/snippet}

  <div class="mx-auto flex w-full max-w-3xl flex-col gap-6">
    {#if complete}
      <Card.Root variant="success" size="sm" data-testid="hub-all-set">
        <div class="flex items-center gap-4">
          <CircleCheck class="h-6 w-6 shrink-0 text-success" />
          <div class="flex flex-col gap-1">
            <h1 class="font-brand text-2xl font-light">Setup is complete</h1>
            <p class="text-sm text-muted-foreground">
              Everything here is done or set aside. You can change any of it below.
            </p>
          </div>
        </div>
      </Card.Root>
    {:else}
      <Card.Root size="flush" data-testid="hub-header">
        <div class="flex flex-col gap-2 p-6">
          <h1 class="font-brand font-hairline text-3xl leading-tight md:text-4xl">
            {#if voice.kind === "self"}
              Make Nocturne <em class="not-italic font-light text-primary">yours</em>.
            {:else if voice.kind === "named"}
              Set Nocturne up for <em class="not-italic font-light text-primary">{voice.name}</em>.
            {:else}
              Finish setting up <em class="not-italic font-light text-primary">Nocturne</em>.
            {/if}
          </h1>
          {#if hub}
            <p class="text-sm text-muted-foreground" data-testid="hub-progress">
              {hub.resolvedCount} of {hub.totalCount} set up. Each of these is optional: do what
              helps, and mark the rest "not for me".
            </p>
          {:else}
            <Skeleton class="h-4 w-64" />
          {/if}
        </div>
      </Card.Root>
    {/if}

    <Card.Root size="flush">
      {#if hub}
        <ul class="divide-y">
          {#each hub.items ?? [] as item (item.key)}
            <li><HubItemRow {item} {voice} /></li>
          {/each}
        </ul>
      {:else}
        <div class="flex flex-col gap-3 p-6">
          {#each [0, 1, 2] as row (row)}
            <Skeleton class="h-10 w-full" />
          {/each}
        </div>
      {/if}
    </Card.Root>

    <HubBasics {voice} />

    <div class="flex justify-end">
      <Button variant="outline" href={resolve("/")}>
        Go to dashboard
        <ArrowRight class="h-4 w-4" />
      </Button>
    </div>
  </div>
</SetupChrome>
