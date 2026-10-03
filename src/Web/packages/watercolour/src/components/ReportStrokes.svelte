<script lang="ts">
  import { reportStripCrop, reportStrokesScene } from '../api/report-header-scene';
  import { type Surface, seedFromName } from '../types';
  import { hostSurface, mountPlayer, watchSurface } from './helpers';

  let {
    name,
    position = 'relative',
    class: className = '',
  }: {
    /** Seeds the strokes, so one name always paints the same three. */
    name: string;
    /** The frame's `position`; see Artwork. */
    position?: 'relative' | 'absolute';
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
      scene: (module, width, height, dpr) => reportStrokesScene(module, width, height, { seed, surface: ground, dpr }),
      crop: reportStripCrop,
      fit: 'fill',
      releaseAfterFinish: true,
    });
  });
</script>

<div bind:this={frame} aria-hidden="true" style:position class="overflow-hidden {className}">
  <canvas bind:this={canvas} class="block h-full w-full"></canvas>
</div>
