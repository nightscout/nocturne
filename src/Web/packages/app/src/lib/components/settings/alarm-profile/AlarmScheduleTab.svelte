<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Switch } from "$lib/components/ui/switch";
  import { Label } from "$lib/components/ui/label";
  import { Separator } from "$lib/components/ui/separator";
  import { Clock } from "lucide-svelte";
  import type { AlarmProfileConfiguration } from "$lib/types/alarm-profile";
  import DayOfWeekPicker from "./DayOfWeekPicker.svelte";

  interface Props {
    profile: AlarmProfileConfiguration;
  }

  let { profile = $bindable() }: Props = $props();

  function addTimeRange() {
    profile.schedule.activeRanges = [
      ...profile.schedule.activeRanges,
      { startTime: "09:00", endTime: "17:00" },
    ];
  }

  function removeTimeRange(index: number) {
    profile.schedule.activeRanges =
      profile.schedule.activeRanges.filter((_, i) => i !== index);
  }
</script>

<div class="space-y-6">
  <div class="flex items-center justify-between">
    <div class="flex items-center gap-3">
      <Clock class="h-5 w-5 text-muted-foreground" />
      <div>
        <Label>Time-Based Schedule</Label>
        <p class="text-sm text-muted-foreground">
          Only activate alarm during specific times
        </p>
      </div>
    </div>
    <Switch bind:checked={profile.schedule.enabled} />
  </div>

  {#if profile.schedule.enabled}
    <Separator />

    <div class="space-y-4">
      <div class="space-y-2">
        <Label>Active Days</Label>
        <DayOfWeekPicker bind:activeDays={profile.schedule.activeDays} />
        <p class="text-xs text-muted-foreground">
          Click to toggle. If none selected, alarm is active every day.
        </p>
      </div>

      <div class="space-y-2">
        <div class="flex items-center justify-between">
          <Label>Active Time Ranges</Label>
          <Button variant="outline" size="sm" onclick={addTimeRange}>
            + Add Range
          </Button>
        </div>
        <div class="space-y-2">
          {#each profile.schedule.activeRanges as range, index (range)}
            <div
              class="flex items-center gap-2 p-3 bg-muted/50 rounded-lg"
            >
              <Input
                type="time"
                bind:value={range.startTime}
                class="w-32"
              />
              <span class="text-muted-foreground">to</span>
              <Input
                type="time"
                bind:value={range.endTime}
                class="w-32"
              />
              {#if profile.schedule.activeRanges.length > 1}
                <Button
                  variant="ghost-destructive"
                  size="icon"
                  onclick={() => removeTimeRange(index)}
                >
                  ×
                </Button>
              {/if}
            </div>
          {/each}
        </div>
      </div>
    </div>
  {:else}
    <div class="text-center py-8 text-muted-foreground">
      <Clock class="h-12 w-12 mx-auto mb-4 opacity-50" />
      <p>This alarm is always active</p>
      <p class="text-sm">
        Enable scheduling to restrict when this alarm can trigger
      </p>
    </div>
  {/if}
</div>
