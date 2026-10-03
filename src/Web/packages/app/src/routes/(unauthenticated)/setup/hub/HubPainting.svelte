<script lang="ts">
  import { untrack } from "svelte";
  import {
    Artwork,
    hostSurface,
    prefersReducedMotion,
    watchSurface,
    type ArtworkPlayer,
  } from "@nocturne/watercolour";
  import { HUB_PAINTING_STOPS } from "$lib/setup-hub/items.svelte";

  let {
    stop,
    from,
    onpainted,
    class: className,
  }: {
    /** 0 to {@link HUB_PAINTING_STOPS}; see `hubPaintingStop`. */
    stop: number;
    /** A stop already seen, to paint forward from rather than jump past. */
    from?: number;
    /** Called with the stop once it is on the canvas, or at once when no stop can be shown. */
    onpainted?: (stop: number) => void;
    class?: string;
  } = $props();

  let surface = $state(hostSurface());
  $effect(() => watchSurface((next) => (surface = next)));

  let player = $state.raw<ArtworkPlayer | null>(null);
  function handleReady(ready: ArtworkPlayer) {
    player = ready;
    return () => {
      if (player === ready) player = null;
    };
  }

  // A still is the finished picture whatever the stop, which would claim everything is set up.
  // Under reduced motion the baked strip stands in: it jumps to a stop without animating.
  const mode = prefersReducedMotion() ? "baked" : "auto";
  const linear = (t: number) => t;

  /** The stop on the canvas, so a later stop is painted forward from it rather than jumped to. */
  let painted: number | undefined = untrack(() => from);
  let seekable = $state(true);

  $effect(() => {
    const current = player;
    const target = stop;
    if (!current) return;

    const { mode: resolved, motion } = current.state;
    seekable = resolved === "live" || resolved === "baked";
    // Untracked: the host's state written in the callback must not become this effect's input.
    const reached = () => untrack(() => onpainted?.(target));
    if (!seekable) {
      reached();
      return;
    }

    const previous = painted;
    painted = target;
    const position = target / HUB_PAINTING_STOPS;

    let frame: number;
    const watchSeek = () => {
      if (!current.state.seeking) { reached(); return; }
      frame = requestAnimationFrame(watchSeek);
    };
    if (previous === undefined || previous >= target || motion === "reduced") {
      current.pause();
      current.seekTo(position);
      frame = requestAnimationFrame(watchSeek);
    } else {
      current.seekTo(previous / HUB_PAINTING_STOPS);
      current.play();
      frame = requestAnimationFrame(function watch() {
        const state = current.state;
        if (!state.seeking && (state.progress >= position || !state.playing)) {
          current.pause();
          current.seekTo(position);
          frame = requestAnimationFrame(watchSeek);
          return;
        }
        frame = requestAnimationFrame(watch);
      });
    }
    return () => cancelAnimationFrame(frame);
  });
</script>

<Artwork
  artwork="hub-dawn-ridges"
  palette="dusk"
  {surface}
  {mode}
  easing={linear}
  durationMs={9000}
  autoplay="never"
  onready={handleReady}
  class="{seekable ? '' : 'hidden'} {className ?? ''}"
/>
