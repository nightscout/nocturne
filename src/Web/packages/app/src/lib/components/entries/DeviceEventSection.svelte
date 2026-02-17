<script lang="ts">
  import type { DeviceEvent } from "$lib/api";
  import { DeviceEventType } from "$lib/api";
  import { Label } from "$lib/components/ui/label";
  import { Button } from "$lib/components/ui/button";
  import { Textarea } from "$lib/components/ui/textarea";
  import * as Select from "$lib/components/ui/select";
  import { Smartphone, X } from "lucide-svelte";

  interface Props {
    deviceEvent: Partial<DeviceEvent>;
    onRemove?: () => void;
  }

  let { deviceEvent = $bindable(), onRemove }: Props = $props();

  const deviceEventTypeLabels: Record<DeviceEventType, string> = {
    [DeviceEventType.SensorStart]: "Sensor Start",
    [DeviceEventType.SensorChange]: "Sensor Change",
    [DeviceEventType.SensorStop]: "Sensor Stop",
    [DeviceEventType.SiteChange]: "Site Change",
    [DeviceEventType.InsulinChange]: "Insulin Change",
    [DeviceEventType.PumpBatteryChange]: "Pump Battery Change",
    [DeviceEventType.PodChange]: "Pod Change",
    [DeviceEventType.ReservoirChange]: "Reservoir Change",
    [DeviceEventType.CannulaChange]: "Cannula Change",
    [DeviceEventType.TransmitterSensorInsert]: "Transmitter/Sensor Insert",
    [DeviceEventType.PodActivated]: "Pod Activated",
    [DeviceEventType.PodDeactivated]: "Pod Deactivated",
    [DeviceEventType.PumpSuspend]: "Pump Suspend",
    [DeviceEventType.PumpResume]: "Pump Resume",
    [DeviceEventType.Priming]: "Priming",
    [DeviceEventType.TubePriming]: "Tube Priming",
    [DeviceEventType.NeedlePriming]: "Needle Priming",
    [DeviceEventType.Rewind]: "Rewind",
    [DeviceEventType.DateChanged]: "Date Changed",
    [DeviceEventType.TimeChanged]: "Time Changed",
    [DeviceEventType.BolusMaxChanged]: "Bolus Max Changed",
    [DeviceEventType.BasalMaxChanged]: "Basal Max Changed",
    [DeviceEventType.ProfileSwitch]: "Profile Switch",
  };
</script>

<div class="space-y-3">
  <div class="flex items-center justify-between">
    <div class="flex items-center gap-2 text-sm font-medium">
      <Smartphone class="h-4 w-4 text-purple-500" />
      Device Event
    </div>
    {#if onRemove}
      <Button variant="ghost" size="icon" class="h-6 w-6" onclick={onRemove}>
        <X class="h-3.5 w-3.5" />
      </Button>
    {/if}
  </div>

  <div class="space-y-1.5">
    <Label for="device-event-type">Event Type</Label>
    <Select.Root
      type="single"
      value={deviceEvent.eventType ?? DeviceEventType.SiteChange}
      onValueChange={(v) => {
        deviceEvent.eventType = v as DeviceEventType;
      }}
    >
      <Select.Trigger id="device-event-type">
        {deviceEventTypeLabels[deviceEvent.eventType ?? DeviceEventType.SiteChange]}
      </Select.Trigger>
      <Select.Content>
        {#each Object.values(DeviceEventType) as det}
          <Select.Item value={det} label={deviceEventTypeLabels[det]} />
        {/each}
      </Select.Content>
    </Select.Root>
  </div>

  <div class="space-y-1.5">
    <Label for="device-event-notes">Notes</Label>
    <Textarea
      id="device-event-notes"
      bind:value={deviceEvent.notes}
      placeholder="Add notes..."
      rows={2}
    />
  </div>
</div>
