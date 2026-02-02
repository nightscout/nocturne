<script lang="ts">
  import { Group } from "layerchart";
  import { NoteCategory } from "$api";
  import {
    Eye,
    HelpCircle,
    CheckSquare,
    Flag,
  } from "lucide-svelte";

  interface Props {
    xPos: number;
    yPos: number;
    category: NoteCategory;
    color: string;
  }

  let { xPos, yPos, category, color }: Props = $props();
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
      {#if category === NoteCategory.Observation}
        <Eye size={16} {color} />
      {:else if category === NoteCategory.Question}
        <HelpCircle size={16} {color} />
      {:else if category === NoteCategory.Task}
        <CheckSquare size={16} {color} />
      {:else if category === NoteCategory.Marker}
        <Flag size={16} {color} />
      {:else}
        <Eye size={16} {color} />
      {/if}
    </div>
  </foreignObject>
</Group>
