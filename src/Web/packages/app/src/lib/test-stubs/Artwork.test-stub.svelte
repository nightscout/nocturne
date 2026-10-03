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
      seeking: false,
    },
    onprogress: undefined as ((progress: number, seeking: boolean) => void) | undefined,
    seekTo(progress: number) {
      fakePlayer.seeks.push(progress);
      fakePlayer.state.progress = progress;
      fakePlayer.state.seeking = true;
    },
    /** Presents a frame; one that shows the latest `seekTo` target is presented with `seeking` false. */
    present(seeking = false) {
      fakePlayer.state.seeking = seeking;
      fakePlayer.onprogress?.(fakePlayer.state.progress, seeking);
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
    onprogress,
    autoplay,
    artwork,
    icon,
    surface,
    mode,
    class: className,
  }: {
    onready?: (player: typeof fakePlayer) => void | (() => void);
    onprogress?: (progress: number, seeking: boolean) => void;
    autoplay?: string;
    artwork?: string;
    icon?: { name: string };
    surface?: string;
    mode?: string;
    class?: string;
  } = $props();

  $effect(() => untrack(() => onready?.(fakePlayer)));
  $effect(() => {
    fakePlayer.onprogress = onprogress;
  });
</script>

<div
  data-testid="artwork"
  data-artwork={artwork ?? icon?.name}
  data-autoplay={autoplay}
  data-surface={surface}
  data-mode={mode}
  class={className}
></div>
