<script lang="ts">
  import { onMount } from "svelte";
  import { Input } from "$lib/components/ui/input";
  import { Switch } from "$lib/components/ui/switch";
  import { Label } from "$lib/components/ui/label";
  import { Separator } from "$lib/components/ui/separator";
  import { Textarea } from "$lib/components/ui/textarea";
  import { Eye } from "lucide-svelte";
  import type {
    AlarmProfileConfiguration,
    EmergencyContactConfig,
  } from "$lib/types/alarm-profile";
  import {
    getBrowserCapabilities,
    type BrowserAlarmCapabilities,
  } from "$lib/audio/alarm-sounds";
  import AlarmPreview from "../AlarmPreview.svelte";
  import CapabilityBadge from "./CapabilityBadge.svelte";

  interface Props {
    profile: AlarmProfileConfiguration;
    emergencyContacts?: EmergencyContactConfig[];
    isDialogOpen: boolean;
  }

  let { profile = $bindable(), emergencyContacts = [], isDialogOpen }: Props =
    $props();

  let capabilities = $state<BrowserAlarmCapabilities | null>(null);

  onMount(() => {
    capabilities = getBrowserCapabilities();
  });
</script>

<div class="space-y-6 @container">
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
  <div class="flex items-center justify-between">
    <div class="flex items-center gap-3">
      <Eye class="h-5 w-5 text-muted-foreground" />
      <div>
        <Label>Screen Flash</Label>
        <p class="text-sm text-muted-foreground">
          Flash the screen to get attention
        </p>
      </div>
    </div>
    <Switch bind:checked={profile.visual.screenFlash} />
  </div>

  {#if profile.visual.screenFlash}
    <div class="grid gap-4 @sm:grid-cols-2 p-4 bg-muted/50 rounded-lg">
      <div class="space-y-2">
        <Label>Flash Color</Label>
        <div class="flex items-center gap-2">
          <input
            type="color"
            bind:value={profile.visual.flashColor}
            class="w-12 h-10 rounded border cursor-pointer"
          />
          <Input
            bind:value={profile.visual.flashColor}
            class="flex-1"
            placeholder="#ff0000"
          />
        </div>
      </div>
      <div class="space-y-2">
        <Label>Flash Interval</Label>
        <div class="flex items-center gap-2">
          <Input
            type="number"
            bind:value={profile.visual.flashIntervalMs}
            class="w-24"
            min="100"
            step="100"
          />
          <span class="text-sm text-muted-foreground">ms</span>
        </div>
      </div>
    </div>
  {/if}

  <Separator />

  <div class="flex items-center justify-between">
    <div>
      <div class="flex items-center gap-2">
        <Label>Persistent Banner</Label>
        <CapabilityBadge {capabilities} feature="notifications" />
      </div>
      <p class="text-sm text-muted-foreground">
        Show notification banner until acknowledged
      </p>
    </div>
    <Switch bind:checked={profile.visual.persistentBanner} />
  </div>

  <div class="flex items-center justify-between">
    <div>
      <div class="flex items-center gap-2">
        <Label>Wake Screen</Label>
        <CapabilityBadge {capabilities} feature="wakeLock" />
      </div>
      <p class="text-sm text-muted-foreground">
        Turn on the screen when alarm triggers
      </p>
    </div>
    <Switch bind:checked={profile.visual.wakeScreen} />
  </div>

  <Separator />

  <div class="flex items-center justify-between">
    <div>
      <Label>Show Emergency Contacts</Label>
      <p class="text-sm text-muted-foreground">
        Display "In Case of Emergency, Contact:" info during alarm
      </p>
    </div>
    <Switch bind:checked={profile.visual.showEmergencyContacts} />
  </div>

  {#if profile.visual.showEmergencyContacts}
    <div class="space-y-2 animate-in slide-in-from-top-2 duration-200">
      <Label>Specific Instructions</Label>
      <Textarea
        bind:value={profile.visual.emergencyInstructions}
        placeholder="e.g. Spare key is under the mat..."
        class="resize-none h-24"
      />
      <p class="text-xs text-muted-foreground">
        Instructions to display to your emergency contacts.
      </p>
    </div>
  {/if}
</div>
