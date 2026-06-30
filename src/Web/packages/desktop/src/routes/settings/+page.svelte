<script lang="ts">
  import { invoke } from "@tauri-apps/api/core";
  import { listen } from "@tauri-apps/api/event";
  import { getVersion } from "@tauri-apps/api/app";
  import { goto } from "$app/navigation";
  import { onMount, onDestroy } from "svelte";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "@nocturne/ui/ui/card";
  import { Button } from "@nocturne/ui/ui/button";
  import { Switch } from "@nocturne/ui/ui/switch";
  import { Label } from "@nocturne/ui/ui/label";
  import * as Select from "@nocturne/ui/ui/select";
  import { Alert, AlertDescription } from "@nocturne/ui/ui/alert";
  import { ArrowLeft, Loader2, RotateCw, Settings as SettingsIcon } from "@lucide/svelte";
  import type { GlucoseUnits } from "@nocturne/ui/glucose";
  import type { ClockFaceOption } from "$lib/glucose-types";
  import { preferences } from "$lib/preferences.svelte";
  import CareLinkConnect from "$lib/CareLinkConnect.svelte";
  import GlucoseCompanion from "$lib/GlucoseCompanion.svelte";

  type CommandError = { message: string };

  const UNIT_OPTIONS: { value: GlucoseUnits; label: string }[] = [
    { value: "mmol", label: "mmol/L" },
    { value: "mg/dl", label: "mg/dL" },
  ];

  let runOnStartup = $state(false);
  let savingStartup = $state(false);
  let version = $state<string | null>(null);
  let error = $state<string | null>(null);

  // Floating clock
  let linked = $state(false);
  let clockFaces = $state<ClockFaceOption[]>([]);
  let loadingClocks = $state(false);
  let clocksError = $state<string | null>(null);
  let floatOpen = $state(false);
  let togglingFloat = $state(false);
  let unlistenFloatClosed: (() => void) | undefined;
  let unlistenLinked: (() => void) | undefined;

  const selectedClockName = $derived(
    clockFaces.find((c) => c.id === preferences.clockId)?.name,
  );

  function describeError(e: unknown): string {
    return (e as CommandError)?.message ?? "Something went wrong.";
  }

  async function refreshClocks() {
    loadingClocks = true;
    clocksError = null;
    try {
      clockFaces = await invoke<ClockFaceOption[]>("list_clock_faces");
    } catch (e) {
      clocksError = describeError(e);
    } finally {
      loadingClocks = false;
    }
  }

  function selectClock(value: string | undefined) {
    if (value) preferences.clockId = value;
  }

  async function toggleFloat(next: boolean) {
    togglingFloat = true;
    error = null;
    try {
      await invoke(next ? "open_floating_clock" : "close_floating_clock");
      floatOpen = next;
    } catch (e) {
      error = describeError(e);
    } finally {
      togglingFloat = false;
    }
  }

  async function toggleStartup(next: boolean) {
    // Optimistic flip; revert if the OS call fails so the switch reflects real state.
    const previous = runOnStartup;
    runOnStartup = next;
    savingStartup = true;
    error = null;
    try {
      await invoke("set_run_on_startup", { enabled: next });
    } catch (e) {
      runOnStartup = previous;
      error = describeError(e);
    } finally {
      savingStartup = false;
    }
  }

  onMount(async () => {
    try {
      runOnStartup = await invoke<boolean>("get_run_on_startup");
    } catch (e) {
      error = describeError(e);
    }
    try {
      version = await getVersion();
    } catch {
      // Version is informational — leave it blank if unavailable.
    }
    try {
      linked = await invoke<boolean>("companion_is_linked");
    } catch {
      // Treat an unreadable link state as not linked — the floating clock stays gated.
    }
    if (linked) refreshClocks();
    try {
      floatOpen = await invoke<boolean>("is_floating_clock_open");
    } catch {
      // Default to closed if the window state can't be read.
    }
    // The overlay can close itself (double-click); reflect that in the toggle.
    unlistenFloatClosed = await listen("floating-clock-closed", () => {
      floatOpen = false;
    });
    // If a source is linked while Settings is open, enable the picker and load the user's clocks.
    unlistenLinked = await listen("companion-linked", () => {
      linked = true;
      refreshClocks();
    });
  });

  onDestroy(() => {
    unlistenFloatClosed?.();
    unlistenLinked?.();
  });
</script>

<main class="mx-auto flex min-h-screen max-w-md flex-col gap-6 p-6">
  <header class="flex items-center gap-3">
    <Button variant="ghost" size="icon" onclick={() => goto("/")} aria-label="Back">
      <ArrowLeft class="h-4 w-4" />
    </Button>
    <div class="flex items-center gap-2">
      <SettingsIcon class="text-primary h-5 w-5" />
      <h1 class="text-lg font-semibold">Settings</h1>
    </div>
  </header>

  {#if error}
    <Alert variant="destructive">
      <AlertDescription>{error}</AlertDescription>
    </Alert>
  {/if}

  <Card>
    <CardHeader>
      <CardTitle>Startup</CardTitle>
      <CardDescription>Control how the Companion behaves when you log in.</CardDescription>
    </CardHeader>
    <CardContent>
      <div class="flex items-start justify-between gap-4">
        <div class="space-y-1">
          <Label for="run-on-startup">Run on startup</Label>
          <p class="text-muted-foreground text-xs">
            Start the Companion in the tray when you log in to Windows, so glucose keeps syncing
            without opening the app.
          </p>
        </div>
        <Switch
          id="run-on-startup"
          checked={runOnStartup}
          disabled={savingStartup}
          onCheckedChange={toggleStartup}
        />
      </div>
    </CardContent>
  </Card>

  <Card>
    <CardHeader>
      <CardTitle>Display</CardTitle>
      <CardDescription>How readings are shown in the Companion.</CardDescription>
    </CardHeader>
    <CardContent class="space-y-2">
      <Label>Glucose units</Label>
      <div class="flex gap-2">
        {#each UNIT_OPTIONS as option (option.value)}
          <Button
            variant={preferences.units === option.value ? "default" : "outline"}
            size="sm"
            onclick={() => (preferences.units = option.value)}
          >
            {option.label}
          </Button>
        {/each}
      </div>
    </CardContent>
  </Card>

  <Card>
    <CardHeader>
      <CardTitle>Floating clock</CardTitle>
      <CardDescription>
        Show a Nocturne clock face in an always-on-top window. Create and style clocks in Nocturne
        under Clock, then paste the clock's link here.
      </CardDescription>
    </CardHeader>
    <CardContent class="space-y-4">
      <div class="space-y-2">
        <Label>Clock face</Label>
        {#if !linked}
          <p class="text-muted-foreground text-xs">Connect a data source first.</p>
        {:else if loadingClocks}
          <p class="text-muted-foreground flex items-center gap-2 text-xs">
            <Loader2 class="h-3.5 w-3.5 animate-spin" /> Loading clocks…
          </p>
        {:else if clocksError}
          <p class="text-destructive text-xs">{clocksError}</p>
          <Button variant="outline" size="sm" onclick={refreshClocks}>
            <RotateCw class="mr-2 h-3.5 w-3.5" /> Retry
          </Button>
        {:else if clockFaces.length === 0}
          <p class="text-muted-foreground text-xs">
            No clocks yet. Create one in Nocturne under Clock, then refresh.
          </p>
          <Button variant="outline" size="sm" onclick={refreshClocks}>
            <RotateCw class="mr-2 h-3.5 w-3.5" /> Refresh
          </Button>
        {:else}
          <div class="flex items-center gap-2">
            <Select.Root type="single" value={preferences.clockId} onValueChange={selectClock}>
              <Select.Trigger class="flex-1">
                {selectedClockName ?? "Choose a clock"}
              </Select.Trigger>
              <Select.Content>
                {#each clockFaces as face (face.id)}
                  <Select.Item value={face.id}>{face.name || "Untitled clock"}</Select.Item>
                {/each}
              </Select.Content>
            </Select.Root>
            <Button
              variant="outline"
              size="icon"
              onclick={refreshClocks}
              aria-label="Refresh clocks"
              title="Refresh"
            >
              <RotateCw class="h-4 w-4" />
            </Button>
          </div>
        {/if}
      </div>
      <div class="flex items-start justify-between gap-4">
        <div class="space-y-1">
          <Label for="show-float">Show floating clock</Label>
          <p class="text-muted-foreground text-xs">
            {#if !linked}
              Connect a data source first.
            {:else}
              Drag to move, double-click to close, and adjust opacity from the window.
            {/if}
          </p>
        </div>
        <Switch
          id="show-float"
          checked={floatOpen}
          disabled={!linked || !preferences.clockId || togglingFloat}
          onCheckedChange={toggleFloat}
        />
      </div>
    </CardContent>
  </Card>

  <section class="space-y-4">
    <div>
      <h2 class="text-base font-semibold">Connections</h2>
      <p class="text-muted-foreground text-sm">Connect a data source to sync your glucose.</p>
    </div>
    <CareLinkConnect />
    <GlucoseCompanion />
  </section>

  {#if version}
    <p class="text-muted-foreground text-center text-xs">Nocturne Companion v{version}</p>
  {/if}
</main>
