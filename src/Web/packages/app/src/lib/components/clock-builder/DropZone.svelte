<script lang="ts">
  import { Plus } from "lucide-svelte";

  interface Props {
    position: "top" | "bottom";
    isActive: boolean;
    onDragOver: (e: DragEvent) => void;
    onDragLeave: () => void;
    onDrop: (e: DragEvent) => void;
  }

  let { position, isActive, onDragOver, onDragLeave, onDrop }: Props = $props();
</script>

<div
  class="flex h-8 w-full items-center justify-center rounded-lg border border-dashed border-white/20 text-white/30 transition-colors {isActive
    ? 'border-primary bg-primary/20'
    : 'hover:border-white/40 hover:text-white/50'}"
  ondragover={onDragOver}
  ondragleave={onDragLeave}
  ondrop={onDrop}
  role="button"
  tabindex="0"
>
  <Plus class="size-4" />
  <span class="ml-1 text-xs">New row at {position}</span>
</div>
