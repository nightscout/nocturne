<script lang="ts">
  import Dumbbell from "lucide-svelte/icons/dumbbell";
  import type { IconProps } from "./types";

  let { class: className = "", ...rest }: IconProps = $props();
</script>

<!-- Exercise mode - activity/exercise automation -->
<Dumbbell class={className} {...rest} />
