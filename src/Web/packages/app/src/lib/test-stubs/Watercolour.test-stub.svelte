<!--
  Stands in for the watercolour engine: `Artwork` (this component), `mountPlayer` and `bloomScene`.
  Every mounted player is a live painting a test can play forward; `bloomScene` echoes its options
  as the scene, so a test reads what a bloom was asked to paint.
-->
<script module lang="ts">
  import { SvelteSet } from "svelte/reactivity";
  import type { mountPlayer as MountPlayer, PlayerState, PlayerStateCallback, WasmModule } from "@nocturne/watercolour";

  export interface FakePainting {
    /** `artwork` for an `Artwork`, `scene` for a scene-built player. */
    artwork?: string;
    scene?: unknown;
    paint(progress: number, finished?: boolean): void;
  }

  const live = new SvelteSet<FakePainting>();

  export const engine = {
    live: () => [...live],
  };

  function start(painting: Omit<FakePainting, "paint">, onstatechange?: PlayerStateCallback, onProgress?: (progress: number) => void) {
    const playing: FakePainting = {
      ...painting,
      paint(progress, finished = false) {
        onProgress?.(progress);
        const state: PlayerState = { mode: "live", motion: "full", playing: !finished, finished, progress, easedProgress: progress };
        onstatechange?.(state);
      },
    };
    live.add(playing);
    return () => {
      live.delete(playing);
    };
  }

  export const mountPlayer: typeof MountPlayer = (_frame, _canvas, _id, options, _onready, onstatechange) =>
    start({ scene: options.scene?.({} as WasmModule, 300, 100, 1) }, onstatechange, options.onProgress);

  export const bloomScene = (_module: unknown, _width: number, _height: number, options: unknown) => options;
</script>

<script lang="ts">
  let { artwork, seed, onstatechange }: { artwork?: string; seed?: number; onstatechange?: PlayerStateCallback } = $props();

  $effect(() => start({ artwork }, onstatechange));
</script>

<div data-testid="artwork-{artwork}" data-seed={seed}></div>
