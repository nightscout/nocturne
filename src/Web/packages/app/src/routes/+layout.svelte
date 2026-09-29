<script lang="ts">
  import "../app.css";
  import { onMount } from "svelte";
  import { ModeWatcher } from "mode-watcher";
  import { getEngineHost } from "@nocturne/watercolour";
  import { illustrations } from "$lib/stores/illustrations.svelte";
  import NavigationProgress from "$lib/components/ui/NavigationProgress.svelte";
  import { Toaster } from "$lib/components/ui/sonner";
  import * as alarmState from "$lib/stores/alarm-state.svelte";
  import { setPreferencesContext } from "$lib/stores/appearance-store.svelte";
  import AlarmActiveView from "$lib/components/settings/alarm-preview/AlarmActiveView.svelte";
  import EmergencyOverlay from "$lib/components/settings/alarm-preview/EmergencyOverlay.svelte";

  const activeAlarm = $derived(alarmState.getActiveAlarm());
  const alarmIsFlashing = $derived(alarmState.getIsFlashing());
  let isEmergencyView = $state(false);

  const showEmergencyButton = $derived(
    activeAlarm?.profile.visual.showEmergencyContacts ?? false
  );

  function handleAlarmDismiss() {
    alarmState.dismiss();
    isEmergencyView = false;
  }

  function handleAlarmSnooze(minutes?: number) {
    alarmState.snooze(minutes ?? activeAlarm?.profile.snooze.defaultMinutes ?? 15);
    isEmergencyView = false;
  }

  function handleEmergencyClick() {
    alarmState.dismiss();
    isEmergencyView = true;
  }

  let { children, data } = $props();

  setPreferencesContext(() => ({
    layers: data.displayPreferences ?? [],
    language: data.displayLanguage,
  }));

  // Top level, so the preference is in the library before any child mounts.
  illustrations.init();

  /**
   * The first engine acquire blocks the main thread for a few hundred ms while
   * the wasm loads and WebGPU hands over a device. Paid on a pointer-enter it
   * freezes the transition that pointer started, and a paint drop's backend is
   * settled within 90 ms of the hover or it falls back to a still.
   *
   * Resolves false where there is no GPU, which needs no handling: that is the
   * case the baked and static paths exist for. Skipped where nothing would use
   * the engine (`still`, `off`) or the user asked to save data; a route that
   * wants live artwork then boots the engine on demand.
   */
  $effect(() => {
    if (illustrations.current !== "animated") return;
    const connection: { saveData?: boolean } | undefined = Reflect.get(navigator, "connection");
    if (connection?.saveData) return;
    void getEngineHost().warmWhenIdle();
  });

  // Children mount first, so this marks the whole page live. Server-rendered markup looks the
  // same before it, but a click on it does nothing; a browser driver waits for this instead.
  onMount(() => {
    document.documentElement.dataset.hydrated = "";
  });
</script>

<ModeWatcher />
<NavigationProgress />
<Toaster />

{#if isEmergencyView && activeAlarm}
  <EmergencyOverlay
    profile={activeAlarm.profile}
    enabledContacts={[]}
    onClose={() => (isEmergencyView = false)}
  />
{/if}

{#if activeAlarm && !isEmergencyView}
  <AlarmActiveView
    profile={activeAlarm.profile}
    isFlashing={alarmIsFlashing}
    {showEmergencyButton}
    onSnooze={handleAlarmSnooze}
    onDismiss={handleAlarmDismiss}
    onEmergencyClick={handleEmergencyClick}
  />
{/if}

{@render children()}
