<script lang="ts">
  import { resolve } from "$app/paths";
  import { DropSurface } from "@nocturne/watercolour";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import Check from "@lucide/svelte/icons/check";
  import { Badge } from "$lib/components/ui/badge";
  import { SetupHubItemState, type SetupHubItem } from "$api";
  import { setupHubItems } from "$lib/setup-hub/items.svelte";
  import type { PatientVoice } from "$lib/onboarding/patient-voice.svelte";

  let { item, voice }: { item: SetupHubItem; voice: PatientVoice } = $props();

  const view = $derived(setupHubItems()[item.key!]);
  const done = $derived(item.state === SetupHubItemState.Done);
</script>

<!-- A done item keeps its mark; an open one paints only under the pointer. -->
<DropSurface
  as="a"
  href={resolve("/(unauthenticated)/setup/(guided)/[item]", { item: view.slug })}
  name={view.slug}
  palette={view.palette}
  shown={done ? true : undefined}
  data-testid="hub-item-{view.slug}"
  data-state={item.state}
  class="group block transition-colors hover:bg-accent/50"
  contentClass="grid grid-cols-[auto_1fr_auto] items-center gap-4 px-6 py-4"
>
  <view.icon
    data-drop-obstacle
    class="h-5 w-5 {item.state === SetupHubItemState.NotForMe
      ? 'text-muted-foreground'
      : 'text-primary'}"
  />
  <span class="flex min-w-0 flex-col gap-0.5">
    <span data-drop-obstacle class="text-sm font-medium">{view.title}</span>
    <span data-drop-obstacle class="text-sm text-muted-foreground">
      {view.description(voice)}
    </span>
  </span>
  <span class="flex items-center gap-2">
    {#if done}
      <Badge variant="success"><Check />Done</Badge>
    {:else if item.state === SetupHubItemState.NotForMe}
      <Badge variant="outline">Not for me</Badge>
    {/if}
    <ChevronRight class="h-4 w-4 text-muted-foreground" />
  </span>
</DropSurface>
