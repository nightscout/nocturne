<script lang="ts">
  import { enumValue } from "#lib/components/ui/enum-value.js";
  import type { DeviceEvent } from "#lib/api/index.js";
  import { DeviceEventType } from "#lib/api/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Textarea } from "#lib/components/ui/textarea/index.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import Smartphone from "@lucide/svelte/icons/smartphone";
  import X from "@lucide/svelte/icons/x";

  interface Props {
    deviceEvent: Partial<DeviceEvent>;
    onRemove?: () => void;
  }

  let { deviceEvent = $bindable(), onRemove }: Props = $props();

  const deviceEventTypeLabels: Record<DeviceEventType, string> = {
    [DeviceEventType.SensorStart]: "Sensor start",
    [DeviceEventType.SensorChange]: "Sensor change",
    [DeviceEventType.SensorStop]: "Sensor stop",
    [DeviceEventType.SiteChange]: "Site change",
    [DeviceEventType.InsulinChange]: "Insulin change",
    [DeviceEventType.PumpBatteryChange]: "Pump battery change",
    [DeviceEventType.PodChange]: "Pod change",
    [DeviceEventType.ReservoirChange]: "Reservoir change",
    [DeviceEventType.CannulaChange]: "Cannula change",
    [DeviceEventType.TransmitterSensorInsert]: "Transmitter/sensor insert",
    [DeviceEventType.PodActivated]: "Pod activated",
    [DeviceEventType.PodDeactivated]: "Pod deactivated",
    [DeviceEventType.PumpSuspend]: "Pump suspend",
    [DeviceEventType.PumpResume]: "Pump resume",
    [DeviceEventType.Priming]: "Priming",
    [DeviceEventType.TubePriming]: "Tube priming",
    [DeviceEventType.NeedlePriming]: "Needle priming",
    [DeviceEventType.Rewind]: "Rewind",
    [DeviceEventType.DateChanged]: "Date changed",
    [DeviceEventType.TimeChanged]: "Time changed",
    [DeviceEventType.BolusMaxChanged]: "Bolus max changed",
    [DeviceEventType.BasalMaxChanged]: "Basal max changed",
    [DeviceEventType.ProfileSwitch]: "Profile switch",
  };
</script>

<div class="space-y-3">
  <div class="flex items-center justify-between">
    <div class="flex items-center gap-2 text-sm font-medium">
      <Smartphone class="h-4 w-4 text-entry-device-event" />
      Device event
    </div>
    {#if onRemove}
      <Button variant="ghost" size="icon-xs" onclick={onRemove}>
        <X class="h-3.5 w-3.5" />
      </Button>
    {/if}
  </div>

  <div class="space-y-1.5">
    <Label for="device-event-type">Event type</Label>
    <Select.Root
      type="single"
      value={deviceEvent.eventType ?? DeviceEventType.SiteChange}
      onValueChange={(v) => {
        deviceEvent.eventType = enumValue(DeviceEventType, v) ?? deviceEvent.eventType;
      }}
    >
      <Select.Trigger id="device-event-type">
        {deviceEventTypeLabels[deviceEvent.eventType ?? DeviceEventType.SiteChange]}
      </Select.Trigger>
      <Select.Content>
        {#each Object.values(DeviceEventType) as det (det)}
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
