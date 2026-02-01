<script lang="ts">
  import { Group } from "layerchart";
  import { DeviceEventIcon } from "$lib/components/icons";

  type DeviceEventType =
    | "Sensor Start"
    | "Sensor Change"
    | "Sensor Stop"
    | "Site Change"
    | "Insulin Change"
    | "Pump Battery Change";

  interface Props {
    xPos: number;
    yPos: number;
    eventType: DeviceEventType;
    color: string;
  }

  let { xPos, yPos, eventType, color }: Props = $props();
</script>

<Group x={xPos} y={yPos}>
  <!-- Background circle -->
  <circle
    r="12"
    fill="var(--background)"
    stroke={color}
    stroke-width="2"
    class="opacity-95"
  />
  <!-- Icon using foreignObject to embed Lucide component -->
  <foreignObject x="-10" y="-10" width="20" height="20">
    <div class="flex items-center justify-center w-full h-full">
      <DeviceEventIcon {eventType} size={16} {color} />
    </div>
  </foreignObject>
</Group>
