<script lang="ts">
  import { untrack } from "svelte";
  import { bloomScene, mountPlayer, type PlayerState } from "@nocturne/watercolour";

  interface Props {
    seed: number;
    delta?: number;
    token: string;
    onstatechange: (state: PlayerState) => void;
    onprogress: (progress: number) => void;
  }

  let { seed, delta = 0, token, onstatechange, onprogress }: Props = $props();
  function tokenColour(el: HTMLElement): [number, number, number] {
    const probeCanvas = document.createElement("canvas");
    probeCanvas.width = probeCanvas.height = 1;
    const probe = probeCanvas.getContext("2d", { willReadFrequently: true });
    if (!probe) return [0.5, 0.5, 0.5];
    probe.fillStyle = getComputedStyle(el).getPropertyValue(token).trim() || "#808080";
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
    const colour = tokenColour(el);
    const slope = Number.isFinite(delta) ? Math.min(1, Math.max(-1, delta / 15)) : 0;
    const readingSeed = seed;
    return mountPlayer(
      container, el, undefined,
      {
        scene: (module, width, height, dpr) => bloomScene(module, width, height, { seed: readingSeed, slope, dpr, colour }),
        fit: "fill", mode: "live", durationMs: 5200, tail: 0.5, releaseAfterFinish: true,
        onProgress: (progress) => untrack(() => onprogress(progress)),
      },
      undefined,
      (state) => untrack(() => onstatechange(state)),
    );
  });
</script>

<div bind:this={frame} class="absolute inset-0" aria-hidden="true">
  <canvas bind:this={canvas} class="block size-full"></canvas>
</div>
