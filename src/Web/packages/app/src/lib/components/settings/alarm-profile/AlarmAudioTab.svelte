<script lang="ts">
  import { onMount } from "svelte";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Switch } from "$lib/components/ui/switch";
  import { Label } from "$lib/components/ui/label";
  import { Separator } from "$lib/components/ui/separator";
  import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
    SelectGroup,
    SelectLabel,
  } from "$lib/components/ui/select";
  import {
    Volume2,
    VolumeX,
    Vibrate,
    TrendingUp,
    Music,
    Upload,
  } from "lucide-svelte";
  import type {
    AlarmProfileConfiguration,
    EmergencyContactConfig,
  } from "$lib/types/alarm-profile";
  import { BUILT_IN_SOUNDS } from "$lib/types/alarm-profile";
  import {
    getAllAlarmSounds,
    isCustomSound,
    getBrowserCapabilities,
    type BrowserAlarmCapabilities,
  } from "$lib/audio/alarm-sounds";
  import AlarmPreview from "../AlarmPreview.svelte";
  import CustomSoundUpload from "../CustomSoundUpload.svelte";
  import CapabilityBadge from "./CapabilityBadge.svelte";

  interface Props {
    profile: AlarmProfileConfiguration;
    emergencyContacts?: EmergencyContactConfig[];
    isDialogOpen: boolean;
  }

  let { profile = $bindable(), emergencyContacts = [], isDialogOpen }: Props =
    $props();

  let allSounds = $state<
    Array<{ id: string; name: string; description: string; isCustom: boolean }>
  >([]);
  let showCustomSoundUpload = $state(false);
  let capabilities = $state<BrowserAlarmCapabilities | null>(null);

  onMount(async () => {
    allSounds = await getAllAlarmSounds();
    capabilities = getBrowserCapabilities();
  });

  // Reload sounds when dialog opens
  $effect(() => {
    if (isDialogOpen) {
      getAllAlarmSounds().then((sounds) => {
        allSounds = sounds;
      });
    }
  });

  function getSelectedSoundName(): string {
    const sound = allSounds.find((s) => s.id === profile.audio.soundId);
    if (sound) return sound.name;
    const builtIn = BUILT_IN_SOUNDS.find(
      (s) => s.id === profile.audio.soundId
    );
    return builtIn?.name ?? "Select sound";
  }

  const vibrationPatterns = [
    { value: "short", label: "Short" },
    { value: "long", label: "Long" },
    { value: "sos", label: "SOS" },
    { value: "continuous", label: "Continuous" },
  ];
</script>

<div class="space-y-6 @container">
  <div class="flex items-center justify-between">
    <div class="flex items-center gap-3">
      <Volume2 class="h-5 w-5 text-muted-foreground" />
      <div>
        <Label>Sound Enabled</Label>
        <p class="text-sm text-muted-foreground">
          Play audio when alarm triggers
        </p>
      </div>
    </div>
    <Switch bind:checked={profile.audio.enabled} />
  </div>

  {#if profile.audio.enabled}
    <Separator />

    <div class="space-y-4">
      <div class="space-y-2">
        <div class="flex items-center justify-between">
          <Label>Alarm Sound</Label>
          <Button
            variant="ghost"
            size="sm"
            onclick={() =>
              (showCustomSoundUpload = !showCustomSoundUpload)}
            class="h-7 text-xs"
          >
            <Upload class="h-3 w-3 mr-1" />
            {showCustomSoundUpload ? "Hide" : "Manage"} Custom Sounds
          </Button>
        </div>
        <div class="flex items-center gap-2">
          <Select
            type="single"
            value={profile.audio.soundId}
            onValueChange={(value) => {
              if (value) profile.audio.soundId = value;
            }}
          >
            <SelectTrigger class="flex-1">
              <span class="flex items-center gap-2">
                {#if isCustomSound(profile.audio.soundId)}
                  <Music class="h-3 w-3" />
                {/if}
                {getSelectedSoundName()}
              </span>
            </SelectTrigger>
            <SelectContent>
              <SelectGroup>
                <SelectLabel>Built-in Sounds</SelectLabel>
                {#each BUILT_IN_SOUNDS as sound}
                  <SelectItem value={sound.id}>{sound.name}</SelectItem>
                {/each}
              </SelectGroup>
              {#if allSounds.some((s) => s.isCustom)}
                <SelectGroup>
                  <SelectLabel>Custom Sounds</SelectLabel>
                  {#each allSounds.filter((s) => s.isCustom) as sound}
                    <SelectItem value={sound.id}>
                      <span class="flex items-center gap-2">
                        <Music class="h-3 w-3" />
                        {sound.name}
                      </span>
                    </SelectItem>
                  {/each}
                </SelectGroup>
              {/if}
            </SelectContent>
          </Select>
        </div>
      </div>

      <!-- Custom Sound Upload Section -->
      {#if showCustomSoundUpload}
        <div class="p-4 rounded-lg border bg-muted/30">
          <CustomSoundUpload
            selectedSoundId={profile.audio.soundId}
            onSoundSelected={(id) => {
              profile.audio.soundId = id;
              // Refresh sound list
              getAllAlarmSounds().then(
                (sounds) => (allSounds = sounds)
              );
            }}
          />
        </div>
      {/if}

      <!-- Preview Section -->
      <div class="p-4 rounded-lg border bg-muted/30">
        <div class="flex items-center justify-between mb-3">
          <Label>Test Alarm</Label>
          <span class="text-xs text-muted-foreground">
            Preview sound and visual effects
          </span>
        </div>
        <AlarmPreview
          {profile}
          isOpen={isDialogOpen}
          {emergencyContacts}
        />
      </div>

      <div
        class="flex items-center justify-between p-4 rounded-lg border"
      >
        <div class="flex items-center gap-3">
          <TrendingUp class="h-5 w-5 text-muted-foreground" />
          <div>
            <Label>Ascending Volume</Label>
            <p class="text-sm text-muted-foreground">
              Start quiet and gradually get louder
            </p>
          </div>
        </div>
        <Switch bind:checked={profile.audio.ascendingVolume} />
      </div>

      {#if profile.audio.ascendingVolume}
        <div
          class="grid gap-4 @sm:grid-cols-3 p-4 bg-muted/50 rounded-lg"
        >
          <div class="space-y-2">
            <Label class="text-sm">Start Volume</Label>
            <div class="flex items-center gap-2">
              <Input
                type="number"
                bind:value={profile.audio.startVolume}
                class="w-20"
                min="0"
                max="100"
              />
              <span class="text-sm text-muted-foreground">%</span>
            </div>
          </div>
          <div class="space-y-2">
            <Label class="text-sm">Max Volume</Label>
            <div class="flex items-center gap-2">
              <Input
                type="number"
                bind:value={profile.audio.maxVolume}
                class="w-20"
                min="0"
                max="100"
              />
              <span class="text-sm text-muted-foreground">%</span>
            </div>
          </div>
          <div class="space-y-2">
            <Label class="text-sm">Ramp Duration</Label>
            <div class="flex items-center gap-2">
              <Input
                type="number"
                bind:value={profile.audio.ascendDurationSeconds}
                class="w-20"
                min="5"
              />
              <span class="text-sm text-muted-foreground">sec</span>
            </div>
          </div>
        </div>
      {:else}
        <div class="space-y-2">
          <Label>Volume</Label>
          <div class="flex items-center gap-4">
            <VolumeX class="h-4 w-4 text-muted-foreground" />
            <input
              type="range"
              bind:value={profile.audio.maxVolume}
              min="0"
              max="100"
              class="flex-1 h-2 bg-muted rounded-lg appearance-none cursor-pointer"
            />
            <Volume2 class="h-4 w-4 text-muted-foreground" />
            <span class="text-sm text-muted-foreground w-12">
              {profile.audio.maxVolume}%
            </span>
          </div>
        </div>
      {/if}
    </div>

    <Separator />

    <div class="flex items-center justify-between">
      <div class="flex items-center gap-3">
        <Vibrate class="h-5 w-5 text-muted-foreground" />
        <div>
          <div class="flex items-center gap-2">
            <Label>Vibration</Label>
            <CapabilityBadge {capabilities} feature="vibration" />
          </div>
          <p class="text-sm text-muted-foreground">
            Vibrate device when alarm triggers
          </p>
        </div>
      </div>
      <Switch bind:checked={profile.vibration.enabled} />
    </div>

    {#if profile.vibration.enabled}
      <div class="space-y-2">
        <Label>Vibration Pattern</Label>
        <Select
          type="single"
          value={profile.vibration.pattern}
          onValueChange={(value) => {
            if (value) profile.vibration.pattern = value;
          }}
        >
          <SelectTrigger>
            <span>
              {vibrationPatterns.find(
                (p) => p.value === profile.vibration.pattern
              )?.label}
            </span>
          </SelectTrigger>
          <SelectContent>
            {#each vibrationPatterns as pattern}
              <SelectItem value={pattern.value}>
                {pattern.label}
              </SelectItem>
            {/each}
          </SelectContent>
        </Select>
      </div>
    {/if}
  {/if}
</div>
