<script lang="ts">
  import "../app.css";
  import { onMount } from "svelte";
  import { ModeWatcher } from "mode-watcher";
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
