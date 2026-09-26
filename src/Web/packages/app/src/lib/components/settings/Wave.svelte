<script lang="ts">
  interface Props {
    canvasWidth: number;
    canvasHeight: number;
    color: string;
    gradientColors: string[];
    barWidth: number;
    peaks: number[];
  }

  let {
    canvasWidth,
    canvasHeight,
    color,
    gradientColors,
    barWidth,
    peaks,
  }: Props = $props();

  let canvasEl: HTMLCanvasElement;
  let pixelRatio = $state(1);

  const updateSize = (width: number, height: number, peaks: number[]) => {
    if (!peaks || !canvasEl) return;

    const ctx = canvasEl.getContext("2d");
    if (!ctx) return;

    const displayWidth = Math.round(width / pixelRatio);
    const displayHeight = Math.round(height / pixelRatio);
    ctx.canvas.width = width;
    ctx.canvas.height = height;
    ctx.canvas.style.width = `${displayWidth}px`;
    ctx.canvas.style.height = `${displayHeight}px`;

    clearWave(ctx, width, height);
    if (barWidth) {
      drawBars(ctx, width, peaks);
    } else {
      drawWaves(ctx, width, peaks);
    }
  };

  $effect(() => {
    // Get pixel ratio with fallback for older browsers
    const deviceXDPI = "deviceXDPI" in screen ? screen.deviceXDPI : undefined;
    const logicalXDPI = "logicalXDPI" in screen ? screen.logicalXDPI : undefined;
    pixelRatio =
      window.devicePixelRatio ||
      (typeof deviceXDPI === "number" && typeof logicalXDPI === "number" && deviceXDPI && logicalXDPI
        ? deviceXDPI / logicalXDPI
        : 1);
    updateSize(canvasWidth, canvasHeight, peaks);
  });

  const absMaxf = (values: number[]) => {
    let max = -Infinity;
    for (const i in values) {
      const num = Math.abs(values[i]);
      if (num > max) max = num;
    }
    return max;
  };

  const maxf = (values: number[]) => Math.max(...values);

  const minf = (values: number[]) => Math.min(...values);

  const drawBars = (
    ctx: CanvasRenderingContext2D,
    width: number,
    peaks: number[]
  ) => {
    const params = {
      fillParent: true,
      height: canvasHeight,
      normalize: true,
      pixelRatio,
      barWidth,
      color,
      gradientColors: gradientColors,
    };

    const hasMinVals = peaks.some((val) => val < 0);
    if (hasMinVals) {
      let indexOffset = peaks[0] < 0 ? 1 : 0;
      peaks = peaks.filter((_, index) => (index + indexOffset) % 2 == 0);
    }

    const offset = 0.5 / params.pixelRatio;
    const halfH = params.height / 2;
    const bar = params.barWidth * params.pixelRatio;
    const gap = Math.max(params.pixelRatio, 2);
    const step = bar + gap;
    const absmax = params.normalize ? absMaxf(peaks) : 1;
    const scale = peaks.length / width;

    if (params.gradientColors.length > 0) {
      const gradient = ctx.createLinearGradient(0, 0, canvasWidth, 0);
      params.gradientColors.forEach((color: string, index: number) => {
        const stop = index / (params.gradientColors.length - 1); // Calculate stop dynamically
        gradient.addColorStop(stop, color);
      });
      ctx.fillStyle = gradient;
    } else {
      ctx.fillStyle = params.color;
    }

    for (let i = 0; i < width; i += step) {
      let h = Math.round((peaks[Math.floor(i * scale)] / absmax) * halfH);
      if (h === 0) h = 1;
      ctx.fillRect(i + offset, halfH - h, bar + offset, h * 2);
    }
  };

  const drawWaves = (
    ctx: CanvasRenderingContext2D,
    width: number,
    peaks: number[]
  ) => {
    const params = {
      fillParent: true,
      height: canvasHeight,
      normalize: true,
      pixelRatio,
      color,
      gradientColors: gradientColors,
    };

    const hasMinValues = peaks.some((val) => val < 0);
    if (!hasMinValues) {
      const reflectedPeaks = [];
      for (let i = 0; i < peaks.length; i++) {
        reflectedPeaks[2 * i] = peaks[i];
        reflectedPeaks[2 * i + 1] = -peaks[i];
      }
      peaks = reflectedPeaks;
    }

    const offset = 0.5 / params.pixelRatio;
    const halfH = params.height / 2;
    const length = ~~(peaks.length / 2);
    const scale = params.fillParent && width != length ? width / length : 1;
    const absmax = params.normalize ? Math.max(maxf(peaks), -minf(peaks)) : 1;

    if (params.gradientColors.length > 0) {
      const gradient = ctx.createLinearGradient(0, 0, canvasWidth, 0);
      params.gradientColors.forEach((color: string, index: number) => {
        const stop = index / (params.gradientColors.length - 1); // Calculate stop dynamically
        gradient.addColorStop(stop, color);
      });
      ctx.fillStyle = gradient;
    } else {
      ctx.fillStyle = params.color;
    }

    ctx.beginPath();
    ctx.moveTo(offset, halfH);

    for (let i = 0; i < length; i++) {
      const h = Math.round((peaks[2 * i] / absmax) * halfH);
      ctx.lineTo(i * scale + offset, halfH - h);
    }

    for (let i = length - 1; i >= 0; i--) {
      const h = Math.round((peaks[2 * i + 1] / absmax) * halfH);
      ctx.lineTo(i * scale + offset, halfH - h);
    }

    ctx.fill();
    ctx.fillRect(0, halfH - offset, width, offset);
  };

  const clearWave = (
    ctx: CanvasRenderingContext2D,
    width: number,
    height: number
  ) => {
    ctx.clearRect(0, 0, width, height);
  };

  function resize() {
    updateSize(canvasWidth, canvasHeight, peaks);
  }
</script>

<svelte:window on:resize={resize} />
<canvas bind:this={canvasEl}></canvas>
