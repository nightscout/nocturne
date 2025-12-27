<script lang="ts">
  import PlayCircle from "lucide-svelte/icons/play-circle";
  import type { IconProps } from "./types";

  let { class: className = "", ...rest }: IconProps = $props();
</script>

<!-- Automatic/Closed-loop mode - full automation -->
<PlayCircle class={className} {...rest} />
