<script lang="ts">
  import { browser } from "$app/environment";
  import { resolve } from "$app/paths";
  import ArrowRight from "@lucide/svelte/icons/arrow-right";
  import CircleCheck from "@lucide/svelte/icons/circle-check";
  import { ConfirmationBackground, DropGroup } from "@nocturne/watercolour";
  import { Button } from "$lib/components/ui/button";
  import * as Card from "$lib/components/ui/card";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import { getSetupHub } from "$api/generated/setupHubs.generated.remote";
  import { getPatientRelationship } from "$api/generated/tenantSettings.generated.remote";
  import { patientVoice } from "$lib/onboarding/patient-voice.svelte";
  import { hubPaintingStop, HUB_PAINTING_STOPS } from "$lib/setup-hub/items.svelte";
  import SetupChrome from "../SetupChrome.svelte";
  import HubPainting from "./HubPainting.svelte";
  import HubItemRow from "./HubItemRow.svelte";
  import HubBasics from "./HubBasics.svelte";

  const hubQuery = getSetupHub();
  const relationshipQuery = getPatientRelationship();

  const hub = $derived(hubQuery.current);
  const voice = $derived(patientVoice(relationshipQuery.current));
  const complete = $derived(hub !== undefined && hub.openCount === 0);

  /** How long the wash over the finished painting holds before the card collapses. */
  const WASH_MS = 3200;

  // The stop this tab last showed, so the painting moves on from it when an item was resolved
  // on its guided page. Kept per tab: it only paces the animation.
  const SEEN_KEY = "nocturne.setup-hub.stop";
  const seen = browser ? readSeen() : undefined;

  function readSeen(): number | undefined {
    try {
      const value = sessionStorage.getItem(SEEN_KEY);
      return value === null ? undefined : Number(value);
    } catch {
      return undefined;
    }
  }

  // Only a hub finished since it was last seen open earns the finish; one opened already finished
  // starts collapsed. The finish paints the last stages, then washes, then collapses.
  let sawOpen = seen !== undefined && seen < HUB_PAINTING_STOPS;
  let finishing = $state(false);
  let washing = $state(false);
  $effect(() => {
    if (!hub) return;
    if (!complete) sawOpen = true;
    else if (sawOpen) {
      sawOpen = false;
      finishing = true;
    }
  });

  // Kept as state rather than acted on in the callback, since the painting may report the last
  // stop before the finish has been noticed.
  let paintedStop = $state<number | undefined>(undefined);
  const handlePainted = (at: number) => (paintedStop = at);
  $effect(() => {
    if (!finishing || (paintedStop ?? 0) < HUB_PAINTING_STOPS) return;
    finishing = false;
    washing = true;
  });

  /** A painting that never reports (no backend came up) still gets its wash after this long. */
  const PAINT_LIMIT_MS = 12000;
  $effect(() => {
    if (!finishing) return;
    const timer = setTimeout(() => handlePainted(HUB_PAINTING_STOPS), PAINT_LIMIT_MS);
    return () => clearTimeout(timer);
  });
  $effect(() => {
    if (!washing) return;
    const timer = setTimeout(() => (washing = false), WASH_MS);
    return () => clearTimeout(timer);
  });

  const stop = $derived(hub ? hubPaintingStop(hub.openCount ?? 0) : undefined);
  $effect(() => {
    if (stop === undefined) return;
    try {
      sessionStorage.setItem(SEEN_KEY, String(stop));
    } catch {
      // Without storage the painting jumps to its stop instead of painting towards it.
    }
  });
</script>

<svelte:head>
  <title>Setup - Nocturne</title>
</svelte:head>

<SetupChrome>
  {#snippet actions()}
    <Button variant="ghost-muted" size="xs" href={resolve("/")}>Go to dashboard</Button>
  {/snippet}

  <div class="mx-auto flex w-full max-w-3xl flex-col gap-6">
    {#if complete && !finishing && !washing}
      <Card.Root variant="success" size="sm" data-testid="hub-all-set">
        <div class="flex items-center gap-4">
          <CircleCheck class="h-6 w-6 shrink-0 text-success" />
          <div class="flex flex-col gap-1">
            <h1 class="font-brand text-2xl font-light">All set up</h1>
            <p class="text-sm text-muted-foreground">
              Everything here is done or set aside. You can change any of it below.
            </p>
          </div>
        </div>
      </Card.Root>
    {:else}
      <Card.Root size="flush" class="relative" data-testid="hub-header">
        {#if washing}
          <ConfirmationBackground palette="moss" />
        {/if}
        <div class="relative px-6 pt-6">
          <HubPainting
            stop={stop ?? seen ?? 0.5}
            from={seen}
            onpainted={handlePainted}
            class="aspect-[3/1] w-full"
          />
        </div>
        <div class="relative flex flex-col gap-2 px-6 pb-6">
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
        <DropGroup name="setup hub items">
          <ul class="divide-y">
            {#each hub.items ?? [] as item (item.key)}
              <li><HubItemRow {item} {voice} /></li>
            {/each}
          </ul>
        </DropGroup>
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
