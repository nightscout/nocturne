<script lang="ts">
  import { untrack } from "svelte";
  import { bloomScene, mountPlayer, type PlayerState } from "@nocturne/watercolour";

  /** One bloom per reading: `seed`, `delta` and `token` are read once, when the canvas mounts. */
  interface Props {
    seed: number;
    /** mg/dL since the previous reading; the splotches' line is steepest at ±15. */
    delta: number;
    /** CSS custom property holding the range's colour, e.g. `--glucose-high`. */
    token: string;
    onstatechange: (state: PlayerState) => void;
    onprogress: (progress: number) => void;
  }

  let { seed, delta, token, onstatechange, onprogress }: Props = $props();

  function tokenColour(el: HTMLElement, cssToken: string): [number, number, number] {
    const probe = document.createElement("canvas").getContext("2d", { willReadFrequently: true })!;
    probe.fillStyle = getComputedStyle(el).getPropertyValue(cssToken).trim();
    probe.fillRect(0, 0, 1, 1);
    const [r, g, b] = probe.getImageData(0, 0, 1, 1).data;
    return [r! / 255, g! / 255, b! / 255];
  }

  let canvas: HTMLCanvasElement | undefined = $state();
  let frame: HTMLDivElement | undefined = $state();
  $effect(() => {
    const el = canvas;
    const container = frame;
    if (!el || !container) return;
    const reading = untrack(() => ({
      seed,
      slope: Math.min(1, Math.max(-1, delta / 15)),
      colour: tokenColour(el, token),
    }));
    return mountPlayer(container, el, {
      scene: (module, width, height, dpr) => bloomScene(module, width, height, { ...reading, dpr }),
      fit: "fill", mode: "live", durationMs: 5200, tail: 0.5, releaseAfterFinish: true,
      onProgress: (progress) => untrack(() => onprogress(progress)),
      onStateChange: (state) => untrack(() => onstatechange(state)),
    });
  });
</script>

<div bind:this={frame} class="absolute inset-0" aria-hidden="true" data-testid="glucose-tile-bloom">
  <canvas bind:this={canvas} class="block size-full"></canvas>
</div>
