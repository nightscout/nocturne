<script lang="ts">
  import { reportWashCrop, reportWashScene } from '../api/report-wash-scene';
  import { type Surface, seedFromName } from '../types';
  import { type PlayerReadyCallback, hostSurface, mountPlayer, watchSurface } from './helpers';

  let {
    name,
    position = 'relative',
    onready,
    class: className = '',
  }: {
    /** Seeds the wash, so one name always paints the same. */
    name: string;
    /** The frame's `position`; see Artwork. */
    position?: 'relative' | 'absolute';
    onready?: PlayerReadyCallback;
    class?: string;
  } = $props();

  let frame: HTMLDivElement | undefined = $state();
  let canvas: HTMLCanvasElement | undefined = $state();
  let surface: Surface | undefined = $state();

  $effect(() => {
    surface = hostSurface();
    return watchSurface((next) => (surface = next));
  });

  $effect(() => {
    if (!frame || !canvas || !surface) return;
    const seed = seedFromName(name);
    const ground = surface;
    return mountPlayer(frame, canvas, {
      scene: (module, width, height, dpr) => reportWashScene(module, width, height, { seed, surface: ground, dpr }),
      crop: reportWashCrop,
      fit: 'fill',
      releaseAfterFinish: true,
      onReady: onready,
    });
  });
</script>

<div bind:this={frame} aria-hidden="true" style:position class="overflow-hidden {className}">
  <canvas bind:this={canvas} class="block h-full w-full"></canvas>
</div>
