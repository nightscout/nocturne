<script lang="ts">
  import { Input } from "$lib/components/ui/input";
  import { Switch } from "$lib/components/ui/switch";
  import { Label } from "$lib/components/ui/label";
  import { Separator } from "$lib/components/ui/separator";
  import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
  } from "$lib/components/ui/select";
  import { Timer, RotateCcw, Sparkles } from "lucide-svelte";
  import type { AlarmProfileConfiguration } from "$lib/types/alarm-profile";

  interface Props {
    profile: AlarmProfileConfiguration;
  }

  let { profile = $bindable() }: Props = $props();

  function addSnoozeOption(minutes: number) {
    if (!profile.snooze.options.includes(minutes)) {
      profile.snooze.options = [
        ...profile.snooze.options,
        minutes,
      ].sort((a, b) => a - b);
    }
  }

  function removeSnoozeOption(minutes: number) {
    profile.snooze.options = profile.snooze.options.filter(
      (m) => m !== minutes
    );
  }
</script>

<div class="space-y-6 @container">
  <div class="space-y-4">
    <h4 class="font-medium flex items-center gap-2">
      <Timer class="h-4 w-4" />
      Snooze Settings
    </h4>

    <div class="grid gap-4 @sm:grid-cols-2">
      <div class="space-y-2">
        <Label>Default Snooze</Label>
        <div class="flex items-center gap-2">
          <Input
            type="number"
            bind:value={profile.snooze.defaultMinutes}
            class="w-24"
            min="1"
          />
          <span class="text-sm text-muted-foreground">minutes</span>
        </div>
      </div>
      <div class="space-y-2">
        <Label>Maximum Snooze</Label>
        <div class="flex items-center gap-2">
          <Input
            type="number"
            bind:value={profile.snooze.maxMinutes}
            class="w-24"
            min="1"
          />
          <span class="text-sm text-muted-foreground">minutes</span>
        </div>
      </div>
    </div>

    <div class="space-y-2">
      <Label>Quick Snooze Options</Label>
      <div class="flex flex-wrap gap-2">
        {#each profile.snooze.options as minutes}
          <span
            class="bg-primary/10 text-primary px-3 py-1 rounded-full text-sm flex items-center gap-2"
          >
            {minutes}m
            <button
              class="hover:text-destructive"
              onclick={() => removeSnoozeOption(minutes)}
            >
              ×
            </button>
          </span>
        {/each}
        <Select
          type="single"
          onValueChange={(value) => {
            if (value) addSnoozeOption(parseInt(value));
          }}
        >
          <SelectTrigger class="w-24 h-8">
            <span class="text-sm">+ Add</span>
          </SelectTrigger>
          <SelectContent>
            {#each [1, 2, 5, 10, 15, 20, 30, 45, 60, 90, 120] as min}
              <SelectItem value={min.toString()}>{min} min</SelectItem>
            {/each}
          </SelectContent>
        </Select>
      </div>
    </div>
  </div>

  <Separator />

  <div class="space-y-4">
    <div class="flex items-center justify-between">
      <div class="flex items-center gap-3">
        <RotateCcw class="h-5 w-5 text-muted-foreground" />
        <div>
          <Label>Re-raise if Unacknowledged</Label>
          <p class="text-sm text-muted-foreground">
            Repeat alarm if not acknowledged
          </p>
        </div>
      </div>
      <Switch bind:checked={profile.reraise.enabled} />
    </div>

    {#if profile.reraise.enabled}
      <div class="grid gap-4 @sm:grid-cols-2 p-4 bg-muted/50 rounded-lg">
        <div class="space-y-2">
          <Label>Re-raise every</Label>
          <div class="flex items-center gap-2">
            <Input
              type="number"
              bind:value={profile.reraise.intervalMinutes}
              class="w-20"
              min="1"
            />
            <span class="text-sm text-muted-foreground">minutes</span>
          </div>
        </div>
        <div class="flex items-center justify-between">
          <div>
            <Label>Escalate Volume</Label>
            <p class="text-sm text-muted-foreground">
              Get louder each time
            </p>
          </div>
          <Switch bind:checked={profile.reraise.escalate} />
        </div>
      </div>
    {/if}
  </div>

  <Separator />

  <div class="space-y-4">
    <div class="flex items-center justify-between">
      <div class="flex items-center gap-3">
        <Sparkles class="h-5 w-5 text-muted-foreground" />
        <div>
          <Label>Smart Snooze</Label>
          <p class="text-sm text-muted-foreground">
            Auto-extend snooze if trending in correct direction
          </p>
        </div>
      </div>
      <Switch bind:checked={profile.smartSnooze.enabled} />
    </div>

    {#if profile.smartSnooze.enabled}
      <div class="p-4 bg-muted/50 rounded-lg space-y-4">
        <p class="text-sm text-muted-foreground">
          For high alarms: extend if glucose is falling.
          <br />
          For low alarms: extend if glucose is rising.
        </p>
        <div class="grid gap-4 @sm:grid-cols-3">
          <div class="space-y-2">
            <Label class="text-sm">Min Delta</Label>
            <div class="flex items-center gap-2">
              <Input
                type="number"
                bind:value={profile.smartSnooze.minDeltaThreshold}
                class="w-20"
                min="1"
              />
              <span class="text-xs text-muted-foreground">
                mg/dL/5min
              </span>
            </div>
          </div>
          <div class="space-y-2">
            <Label class="text-sm">Extend by</Label>
            <div class="flex items-center gap-2">
              <Input
                type="number"
                bind:value={profile.smartSnooze.extensionMinutes}
                class="w-20"
                min="1"
              />
              <span class="text-sm text-muted-foreground">min</span>
            </div>
          </div>
          <div class="space-y-2">
            <Label class="text-sm">Max Total</Label>
            <div class="flex items-center gap-2">
              <Input
                type="number"
                bind:value={profile.smartSnooze.maxTotalMinutes}
                class="w-20"
                min="1"
              />
              <span class="text-sm text-muted-foreground">min</span>
            </div>
          </div>
        </div>
      </div>
    {/if}
  </div>
</div>
