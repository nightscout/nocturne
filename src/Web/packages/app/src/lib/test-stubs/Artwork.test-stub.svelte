<script lang="ts" module>
  /** Stands in for the `ArtworkPlayer` the real component hands to `onready`. */
  export const fakePlayer = {
    seeks: [] as number[],
    state: { motion: "full" as "full" | "reduced" },
    seek(progress: number) {
      fakePlayer.seeks.push(progress);
    },
  };
</script>

<script lang="ts">
  import { untrack } from "svelte";

  let {
    onready,
    autoplay,
    artwork,
    icon,
  }: {
    onready?: (player: typeof fakePlayer) => void | (() => void);
    autoplay?: string;
    artwork?: string;
    icon?: { name: string };
  } = $props();

  $effect(() => untrack(() => onready?.(fakePlayer)));
</script>

<div
  data-testid="artwork"
  data-artwork={artwork ?? icon?.name}
  data-autoplay={autoplay}
></div>
