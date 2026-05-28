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
  import { Timer } from "lucide-svelte";
  import type {
    AlarmProfileConfiguration,
    AlarmTriggerType,
    AlarmPriority,
  } from "$lib/types/alarm-profile";
  import {
    ALARM_TYPE_LABELS,
    normalizeAlarmType,
    PRIORITY_LABELS,
  } from "$lib/types/alarm-profile";

  interface Props {
    profile: AlarmProfileConfiguration;
  }

  let { profile = $bindable() }: Props = $props();

  const alarmTypes: AlarmTriggerType[] = [
    "UrgentLow",
    "Low",
    "High",
    "UrgentHigh",
    "ForecastLow",
    "RisingFast",
    "FallingFast",
    "StaleData",
    "Custom",
  ];

  const priorities: AlarmPriority[] = ["Low", "Normal", "High", "Critical"];
</script>

<div class="space-y-6 @container">
  <div class="grid gap-4 @sm:grid-cols-2">
    <div class="space-y-2">
      <Label for="name">Alarm Name</Label>
      <Input
        id="name"
        bind:value={profile.name}
        placeholder="e.g., Nighttime Low"
      />
    </div>
    <div class="space-y-2">
      <Label for="description">Description</Label>
      <Input
        id="description"
        bind:value={profile.description}
        placeholder="Optional description"
      />
    </div>
  </div>

  <div class="grid gap-4 @sm:grid-cols-2">
    <div class="space-y-2">
      <Label>Alarm Type</Label>
      <Select
        type="single"
        value={profile.alarmType}
        onValueChange={(value) => {
          if (value) {
            profile.alarmType = normalizeAlarmType(value);

            // Auto-fill emergency instructions for Urgent Low if empty
            if (
              profile.alarmType === "UrgentLow" &&
              !profile.visual.emergencyInstructions
            ) {
              profile.visual.emergencyInstructions =
                "Administer carbs ONLY if they are conscious and able to swallow by themselves.";
              profile.visual.showEmergencyContacts = true;
            }
          }
        }}
      >
        <SelectTrigger>
          <span>{ALARM_TYPE_LABELS[profile.alarmType]}</span>
        </SelectTrigger>
        <SelectContent>
          {#each alarmTypes as type}
            <SelectItem value={type}>
              {ALARM_TYPE_LABELS[type] ?? type}
            </SelectItem>
          {/each}
        </SelectContent>
      </Select>
    </div>
    <div class="space-y-2">
      <Label>Priority</Label>
      <Select
        type="single"
        value={profile.priority}
        onValueChange={(value) => {
          if (value) profile.priority = value as AlarmPriority;
        }}
      >
        <SelectTrigger>
          <span>{PRIORITY_LABELS[profile.priority]}</span>
        </SelectTrigger>
        <SelectContent>
          {#each priorities as priority}
            <SelectItem value={priority}>
              {PRIORITY_LABELS[priority] ?? priority}
            </SelectItem>
          {/each}
        </SelectContent>
      </Select>
    </div>
  </div>

  <Separator />

  <div class="space-y-4">
    <h4 class="font-medium">Threshold Settings</h4>
    <div class="grid gap-4 @sm:grid-cols-2">
      <div class="space-y-2">
        <Label>
          {profile.alarmType === "StaleData"
            ? "Minutes without data"
            : "Threshold"}
        </Label>
        <div class="flex items-center gap-2">
          <Input
            type="number"
            bind:value={profile.threshold}
            class="w-24"
          />
          <span class="text-sm text-muted-foreground">
            {profile.alarmType === "StaleData"
              ? "min"
              : profile.alarmType === "RisingFast" ||
                  profile.alarmType === "FallingFast"
                ? "mg/dL/min"
                : "mg/dL"}
          </span>
        </div>
      </div>

      {#if profile.alarmType === "Custom"}
        <div class="space-y-2">
          <Label>Upper Threshold</Label>
          <div class="flex items-center gap-2">
            <Input
              type="number"
              bind:value={profile.thresholdHigh}
              class="w-24"
            />
            <span class="text-sm text-muted-foreground">mg/dL</span>
          </div>
        </div>
      {/if}

      {#if profile.alarmType === "ForecastLow"}
        <div class="space-y-2">
          <Label>Lead Time</Label>
          <div class="flex items-center gap-2">
            <Input
              type="number"
              bind:value={profile.forecastLeadTimeMinutes}
              class="w-24"
              min="5"
              max="60"
              step="5"
            />
            <span class="text-sm text-muted-foreground">minutes</span>
          </div>
          <p class="text-xs text-muted-foreground">
            How far ahead to predict (5-60 min). Alarm triggers if glucose is forecast to drop below threshold within this time.
          </p>
        </div>
      {/if}
    </div>

    <div class="space-y-2">
      <div class="flex items-center gap-2">
        <Timer class="h-4 w-4 text-muted-foreground" />
        <Label>Delayed Raise (Persistence)</Label>
      </div>
      <p class="text-sm text-muted-foreground mb-2">
        Only trigger alarm if condition persists for this long
      </p>
      <div class="flex items-center gap-2">
        <Input
          type="number"
          bind:value={profile.persistenceMinutes}
          class="w-24"
          min="0"
        />
        <span class="text-sm text-muted-foreground">
          minutes (0 = immediate)
        </span>
      </div>
    </div>
  </div>

  <Separator />

  <div class="flex items-center justify-between">
    <div>
      <Label>Override Quiet Hours</Label>
      <p class="text-sm text-muted-foreground">
        This alarm will sound even during quiet hours
      </p>
    </div>
    <Switch bind:checked={profile.overrideQuietHours} />
  </div>
</div>
