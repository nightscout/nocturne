<script lang="ts">
  import { Group } from "layerchart";
  import { SystemEventIcon } from "$lib/components/icons";
  import { SystemEventType } from "$lib/api";

  interface Props {
    xPos: number;
    yPos: number;
    eventType: SystemEventType;
    color: string;
  }

  let { xPos, yPos, eventType, color }: Props = $props();
</script>

<Group x={xPos} y={yPos}>
  <!-- Icon using foreignObject to embed Lucide component -->
  <foreignObject x="-8" y="-8" width="16" height="16">
    <div class="flex items-center justify-center w-full h-full">
      <SystemEventIcon {eventType} size={16} {color} />
    </div>
  </foreignObject>
</Group>
