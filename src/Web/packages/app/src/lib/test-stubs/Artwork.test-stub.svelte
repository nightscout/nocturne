<script lang="ts" module>
  /** Stands in for the `ArtworkPlayer` the real component hands to `onready`. */
  export const fakePlayer = {
    seeks: [] as number[],
    plays: 0,
    state: {
      mode: "live" as "live" | "baked" | "static" | "none",
      motion: "full" as "full" | "reduced",
      progress: 0,
      playing: false,
    },
    seekTo(progress: number) {
      fakePlayer.seeks.push(progress);
      fakePlayer.state.progress = progress;
    },
    play() {
      fakePlayer.plays += 1;
      fakePlayer.state.playing = true;
    },
    pause() {
      fakePlayer.state.playing = false;
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
    surface,
    mode,
    class: className,
  }: {
    onready?: (player: typeof fakePlayer) => void | (() => void);
    autoplay?: string;
    artwork?: string;
    icon?: { name: string };
    surface?: string;
    mode?: string;
    class?: string;
  } = $props();

  $effect(() => untrack(() => onready?.(fakePlayer)));
</script>

<div
  data-testid="artwork"
  data-artwork={artwork ?? icon?.name}
  data-autoplay={autoplay}
  data-surface={surface}
  data-mode={mode}
  class={className}
></div>
