<script lang="ts">
  import { invoke } from "@tauri-apps/api/core";
  import { listen } from "@tauri-apps/api/event";
  import { goto } from "$app/navigation";
  import { onMount } from "svelte";
  import { Card, CardContent } from "@nocturne/ui/ui/card";
  import { Button } from "@nocturne/ui/ui/button";
  import { Loader2, Monitor, Settings } from "@lucide/svelte";
  import GlucoseReadout from "$lib/GlucoseReadout.svelte";
  import type { Reading } from "$lib/glucose-types";
  import { preferences } from "$lib/preferences.svelte";

  let reading = $state<Reading | null>(null);
  let linked = $state(false);
  let now = $state(Date.now());

  onMount(() => {
    // Seed from the last-written file and the current link state; live values arrive via events.
    invoke<Reading | null>("get_current_glucose")
      .then((r) => (reading = r))
      .catch(() => {});
    invoke<boolean>("companion_is_linked")
      .then((l) => (linked = l))
      .catch(() => {});

    // Re-tick so "x min ago" stays current between readings.
    const timer = setInterval(() => (now = Date.now()), 30_000);
    const unReading = listen<Reading | null>("glucose-updated", (event) => {
      // A poll with no current reading (sensor gap) emits null — keep the last value and let it
      // go stale rather than dropping back to the "waiting" card.
      if (event.payload) {
        reading = event.payload;
        now = Date.now();
      }
    });
    const unLinked = listen("companion-linked", () => {
      linked = true;
    });
    return () => {
      clearInterval(timer);
      unReading.then((fn) => fn());
      unLinked.then((fn) => fn());
    };
  });
</script>

<main class="mx-auto flex min-h-screen max-w-md flex-col gap-6 p-6">
  <header class="flex items-center gap-3">
    <Monitor class="text-primary h-6 w-6" />
    <h1 class="text-lg font-semibold">Nocturne Companion</h1>
    <Button
      variant="ghost"
      size="icon"
      class="ml-auto"
      onclick={() => goto("/settings")}
      aria-label="Settings"
    >
      <Settings class="h-4 w-4" />
    </Button>
  </header>

  {#if reading}
    <GlucoseReadout {reading} units={preferences.units} {now} />
  {:else if linked}
    <Card>
      <CardContent class="flex flex-col items-center gap-3 py-8 text-center">
        <Loader2 class="text-primary h-6 w-6 animate-spin" />
        <p class="text-muted-foreground text-sm">Waiting for the first reading…</p>
      </CardContent>
    </Card>
  {:else}
    <Card>
      <CardContent class="flex flex-col items-center gap-3 py-8 text-center">
        <p class="text-sm font-medium">Not connected</p>
        <p class="text-muted-foreground text-sm">
          Connect a data source to start syncing your glucose.
        </p>
        <Button size="sm" onclick={() => goto("/settings")}>Open Settings</Button>
      </CardContent>
    </Card>
  {/if}
</main>
