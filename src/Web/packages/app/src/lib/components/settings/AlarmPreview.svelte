<script lang="ts">
  import { onDestroy } from "svelte";
  import { Button } from "$lib/components/ui/button";
  import {
    previewAlarmSound,
    stopPreview,
    subscribeToPreviewState,
    type PreviewState,
  } from "$lib/audio/alarm-sounds";
  import type {
    AlarmProfileConfiguration,
    EmergencyContactConfig,
  } from "$lib/types/alarm-profile";
  import { Volume2, VolumeX, Square } from "lucide-svelte";
  import AlarmWaveform from "./AlarmWaveform.svelte";
  import AlarmActiveView from "./alarm-preview/AlarmActiveView.svelte";
  import EmergencyOverlay from "./alarm-preview/EmergencyOverlay.svelte";

  interface Props {
    profile: AlarmProfileConfiguration;
    isOpen?: boolean;
    emergencyContacts?: EmergencyContactConfig[];
  }

  let { profile, isOpen = false, emergencyContacts = [] }: Props = $props();

  let previewState = $state<PreviewState>({ isPlaying: false, soundId: null });
  let isFlashing = $state(false);
  let isEmergencyView = $state(false);

  // Get enabled emergency contacts
  let enabledContacts = $derived(
    emergencyContacts.filter((c) => c.enabled && (c.phone || c.email))
  );

  // Show emergency contacts when playing and option is enabled
  let showEmergencyButton = $derived(
    previewState.isPlaying &&
      profile.visual.showEmergencyContacts &&
      enabledContacts.length > 0
  );

  // Subscribe to preview state
  $effect(() => {
    const unsubscribe = subscribeToPreviewState((state) => {
      previewState = state;
    });
    return unsubscribe;
  });

  // Handle flashing logic
  $effect(() => {
    if (
      previewState.isPlaying &&
      profile.visual.screenFlash &&
      !isEmergencyView
    ) {
      const interval = setInterval(() => {
        isFlashing = !isFlashing;
      }, profile.visual.flashIntervalMs);

      return () => {
        clearInterval(interval);
        isFlashing = false;
      };
    } else {
      isFlashing = false;
    }
  });

  // Stop preview when dialog closes
  $effect(() => {
    if (!isOpen && previewState.isPlaying) {
      stopPreview();
    }
  });

  // Reset emergency view when preview stops
  $effect(() => {
    if (!previewState.isPlaying && !isOpen) {
      isEmergencyView = false;
    }
  });

  onDestroy(() => {
    if (previewState.isPlaying) {
      stopPreview();
    }
  });

  async function handlePreview() {
    if (previewState.isPlaying) {
      stopPreview();
      return;
    }

    await previewAlarmSound(profile.audio.soundId, {
      volume: profile.audio.maxVolume,
      ascending: profile.audio.ascendingVolume,
      startVolume: profile.audio.startVolume,
      ascendDurationSeconds: Math.min(profile.audio.ascendDurationSeconds, 5),
      vibrate: profile.vibration.enabled,
      minDurationSeconds: 4,
    });
  }

  function handleEmergencyClick() {
    stopPreview();
    isEmergencyView = true;
  }
</script>

<!-- Full Screen Emergency View -->
{#if isEmergencyView}
  <EmergencyOverlay
    {profile}
    {enabledContacts}
    onClose={() => (isEmergencyView = false)}
  />
{/if}

<!-- Alarm Banner Overlay -->
{#if (showEmergencyButton || previewState.isPlaying) && !isEmergencyView}
  <AlarmActiveView
    {profile}
    {isFlashing}
    {showEmergencyButton}
    onSnooze={() => stopPreview()}
    onDismiss={() => stopPreview()}
    onEmergencyClick={handleEmergencyClick}
  />
{/if}

<div class="space-y-3">
  <!-- Waveform visualization -->
  <AlarmWaveform
    audioSettings={profile.audio}
    isPlaying={previewState.isPlaying}
    width={280}
    height={64}
  />

  <!-- Preview button -->
  <div class="flex items-center gap-3">
    <Button
      variant={previewState.isPlaying ? "default" : "outline"}
      onclick={handlePreview}
    >
      {#if previewState.isPlaying}
        <Square class="h-4 w-4 fill-current" />
        Stop
      {:else}
        <Volume2 class="h-4 w-4" />
        Play Preview
      {/if}
    </Button>

    {#if profile.audio.ascendingVolume}
      <span class="text-xs text-muted-foreground">
        {profile.audio.startVolume}% → {profile.audio.maxVolume}% over {profile
          .audio.ascendDurationSeconds}s
      </span>
    {:else}
      <span class="text-xs text-muted-foreground">
        Volume: {profile.audio.maxVolume}%
      </span>
    {/if}
  </div>
</div>

{#if !profile.audio.enabled}
  <div class="flex items-center gap-2 text-muted-foreground text-sm mt-2">
    <VolumeX class="h-4 w-4" />
    <span>Sound disabled for this alarm</span>
  </div>
{/if}
