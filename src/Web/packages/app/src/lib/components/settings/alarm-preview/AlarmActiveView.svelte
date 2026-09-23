<script lang="ts">
  import { AlertCircle, Clock } from "lucide-svelte";
  import { Button } from "$lib/components/ui/button";
  import { bg } from "$lib/utils/formatting";
  import type { AlarmProfileConfiguration } from "$lib/types/alarm-profile";

  interface Props {
    profile: AlarmProfileConfiguration;
    isFlashing: boolean;
    showEmergencyButton?: boolean;
    onSnooze: (minutes?: number) => void;
    onDismiss: () => void;
    onEmergencyClick: () => void;
  }

  let {
    profile,
    isFlashing,
    showEmergencyButton = false,
    onSnooze,
    onDismiss,
    onEmergencyClick,
  }: Props = $props();
</script>

<!-- Screen flash overlay -->
{#if isFlashing && profile.visual.screenFlash}
  <div
    class="fixed inset-0 z-100 pointer-events-none bg-(--flash-color) opacity-30 transition-opacity duration-100"
    style:--flash-color={profile.visual.flashColor}
  ></div>
{/if}

<!-- Alarm Banner Overlay -->
<div
  class="fixed inset-0 z-[1001] flex flex-col pointer-events-auto bg-black/80 backdrop-blur-sm animate-in fade-in duration-300"
>
  <!-- Top Alarm Banner -->
  <div
    class="w-full bg-destructive text-destructive-foreground p-6 shadow-2xl animate-in slide-in-from-top duration-500"
  >
    <div class="container mx-auto max-w-lg text-center space-y-4">
      <div
        class="flex items-center justify-center gap-2 text-xl font-bold uppercase tracking-wider opacity-90 animate-pulse"
      >
        <AlertCircle class="h-6 w-6" />
        <span>Alarm: {profile.name}</span>
        <AlertCircle class="h-6 w-6" />
      </div>

      <div class="text-6xl font-black tracking-tighter">
        {profile.alarmType === "StaleData"
          ? `${profile.threshold}m`
          : bg(profile.threshold)}
      </div>

      <div class="text-lg font-medium opacity-80">
        {profile.alarmType === "StaleData"
          ? "No Data Received"
          : "Glucose Alert"}
      </div>

      {#if showEmergencyButton}
        <div class="mt-4 animate-in zoom-in duration-300">
          <Button
            variant="outline-destructive"
            size="xl"
            onclick={onEmergencyClick}
          >
            <AlertCircle />
            Emergency Contacts
          </Button>
        </div>
      {/if}
    </div>
  </div>

  <!-- Actions Area (Bottom) -->
  <div
    class="flex-1 flex flex-col justify-end pb-12 container mx-auto max-w-lg px-4 gap-4"
  >
    {#if profile.snooze.options.length > 0}
      <div class="grid grid-cols-2 gap-3">
        {#each profile.snooze.options as minutes, i (i)}
          <Button
            variant="secondary"
            size="xl"
            onclick={() => onSnooze(minutes)}
          >
            <Clock class="h-5 w-5 mr-2" />
            Snooze {minutes}m
          </Button>
        {/each}
      </div>
    {/if}

    <Button
      variant="destructive"
      size="xl"
      onclick={onDismiss}
    >
      Dismiss Alarm
    </Button>
  </div>
</div>
