<script lang="ts">
  import { untrack } from "svelte";
  import { bloomScene, createArtworkPlayer, type PlayerState } from "@nocturne/watercolour";

  interface Props {
    seed: number;
    delta?: number;
    token: string;
    onstatechange: (state: PlayerState) => void;
  }

  let { seed, delta = 0, token, onstatechange }: Props = $props();
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
  $effect(() => {
    const el = canvas;
    if (!el) return;
    const { width, height } = el.getBoundingClientRect();
    if (width === 0 || height === 0) return;
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    el.width = Math.max(1, Math.round(width * dpr));
    el.height = Math.max(1, Math.round(height * dpr));
    const colour = tokenColour(el);
    const slope = Number.isFinite(delta) ? Math.min(1, Math.max(-1, delta / 15)) : 0;
    const player = createArtworkPlayer(
      el,
      { scene: (module) => bloomScene(module, width, height, { seed, slope, dpr, colour }) },
      { mode: "live", durationMs: 5200, tail: 0.5, width, height, dpr, releaseAfterFinish: true },
    );
    // The initial callback must not make host state a dependency of this effect.
    const emit = () => untrack(() => onstatechange(player.state));
    const offs = [player.on("ready", emit), player.on("statechange", emit), player.on("fallback", emit)];
    emit();
    return () => {
      for (const off of offs) off();
      player.dispose();
    };
  });
</script>

<canvas bind:this={canvas} class="absolute inset-0 block size-full" aria-hidden="true"></canvas>
