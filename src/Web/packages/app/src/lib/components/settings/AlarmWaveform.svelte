<script module lang="ts">
  interface CachedSoundData {
    peaks: number[];
    duration: number;
  }
  /** Module-level cache for computed peaks (persists across component remounts) */
  // eslint-disable-next-line svelte/prefer-svelte-reactivity -- module-level cache; nothing renders from it
  const peaksCache = new Map<string, CachedSoundData>();
</script>

<script lang="ts">
  import { onMount } from "svelte";
  import {
    subscribeToPreviewState,
    isCustomSound,
    getCustomSound,
    type PreviewState,
  } from "$lib/audio/alarm-sounds";
  import type { AlarmAudioSettings } from "$lib/types/alarm-profile";

  interface Props {
    audioSettings: AlarmAudioSettings;
    isPlaying?: boolean;
    width?: number;
    height?: number;
    color?: string;
    progressColor?: string;
    barWidth?: number;
  }

  let {
    audioSettings,
    isPlaying = false,
    width = 200,
    height = 60,
    color = "hsl(var(--muted-foreground) / 0.4)",
    progressColor = "hsl(var(--primary))",
    barWidth = 3,
  }: Props = $props();

  let canvasEl: HTMLCanvasElement | undefined = $state();
  let progressCanvasEl: HTMLCanvasElement | undefined = $state();
  let animationFrame: number | null = null;
  let startTime = $state(0);
  let previewState = $state<PreviewState>({ isPlaying: false, soundId: null });
  let pixelRatio = $state(1);

  // Computed: whether we're actively playing
  let playing = $derived(isPlaying || previewState.isPlaying);

  // Waveform peaks - actual audio data or generated for synthesized sounds
  let peaks = $state<number[]>([]);
  let audioDuration = $state(0);

  // Progress position (0-1)
  let progressPosition = $state(0);

  function getPeaksFromBuffer(
    buffer: AudioBuffer,
    numberOfBuckets: number = 64,
    channel: number = 0
  ): number[] {
    if (channel >= buffer.numberOfChannels) {
      channel = 0;
    }

    const decodedAudioData = buffer.getChannelData(channel);
    const bucketDataSize = Math.floor(
      decodedAudioData.length / numberOfBuckets
    );
    const buckets: number[] = [];

    for (let i = 0; i < numberOfBuckets; i++) {
      const startingPoint = i * bucketDataSize;
      const endingPoint = startingPoint + bucketDataSize;

      let max = 0;
      for (let j = startingPoint; j < endingPoint; j++) {
        const absolute = Math.abs(decodedAudioData[j]);
        if (absolute > max) {
          max = absolute;
        }
      }

      buckets.push(max);
    }

    // Normalize peaks
    const maxPeak = Math.max(...buckets) || 1;
    return buckets.map((p) => p / maxPeak);
  }

  /** Load peaks from custom audio file */
  async function loadCustomSoundPeaks(
    soundId: string
  ): Promise<CachedSoundData> {
    // Check cache first
    if (peaksCache.has(soundId)) {
      return peaksCache.get(soundId)!;
    }

    const sound = await getCustomSound(soundId);
    if (!sound) {
      return generateSynthesizedPeaks(soundId);
    }

    try {
      // Decode the audio data
      const audioContext = new AudioContext();

      // Convert data URL to array buffer
      const response = await fetch(sound.dataUrl);
      const arrayBuffer = await response.arrayBuffer();
      const audioBuffer = await audioContext.decodeAudioData(arrayBuffer);

      const extractedPeaks = getPeaksFromBuffer(audioBuffer, 64);
      const duration = audioBuffer.duration;

      const data = { peaks: extractedPeaks, duration };

      // Cache the result in memory
      peaksCache.set(soundId, data);

      await audioContext.close();
      return data;
    } catch (err) {
      console.error("Failed to extract peaks from audio:", err);
      return generateSynthesizedPeaks(soundId);
    }
  }

  /**
   * Generate waveform peaks for synthesized (built-in) sounds These sounds are
   * generated via Web Audio API oscillators, so we create representative
   * waveform patterns based on their characteristics
   */
  function generateSynthesizedPeaks(
    soundId: string,
    numberOfBuckets: number = 64
  ): CachedSoundData {
    const generatedPeaks: number[] = [];

    // Different waveform patterns for different alarm types
    let pattern: (i: number, total: number) => number;

    if (soundId.includes("urgent") || soundId.includes("siren")) {
      // Aggressive, spiky waveform for urgent alarms
      pattern = (i, total) => {
        const t = i / total;
        const spike = Math.sin(t * Math.PI * 12) * 0.3;
        const base = 0.6 + Math.sin(t * Math.PI * 2) * 0.2;
        return Math.abs(base + spike);
      };
    } else if (soundId.includes("low")) {
      // Deeper, rounder waveform for low alerts
      pattern = (i, total) => {
        const t = i / total;
        return (
          0.4 +
          Math.sin(t * Math.PI * 3) * 0.25 +
          Math.sin(t * Math.PI * 7) * 0.15
        );
      };
    } else if (soundId.includes("high")) {
      // Rising pattern for high alerts
      pattern = (i, total) => {
        const t = i / total;
        const rise = t * 0.3;
        return 0.4 + rise + Math.sin(t * Math.PI * 5) * 0.2;
      };
    } else if (
      soundId.includes("chime") ||
      soundId.includes("soft") ||
      soundId.includes("bell")
    ) {
      // Gentle, smooth waveform
      pattern = (i, total) => {
        const t = i / total;
        const decay = Math.exp(-t * 2);
        return 0.3 + decay * 0.5 + Math.sin(t * Math.PI * 4) * 0.1 * decay;
      };
    } else if (soundId.includes("beep")) {
      // Square-ish beep pattern
      pattern = (i, total) => {
        const t = i / total;
        const beepPos = (t * 3) % 1;
        return beepPos < 0.4 ? 0.7 : 0.2;
      };
    } else {
      // Default balanced waveform
      pattern = (i, total) => {
        const t = i / total;
        return (
          0.5 +
          Math.sin(t * Math.PI * 6) * 0.25 +
          Math.sin(t * Math.PI * 13) * 0.1
        );
      };
    }

    for (let i = 0; i < numberOfBuckets; i++) {
      const value = pattern(i, numberOfBuckets);
      // Add slight variation
      const variation = Math.sin(i * 7.3) * 0.05;
      generatedPeaks.push(Math.max(0.15, Math.min(1, value + variation)));
    }

    return { peaks: generatedPeaks, duration: 5 }; // Assume 5s loop for synthesized
  }

  /** Load peaks for the current sound (custom or synthesized) */
  async function loadPeaks(soundId: string): Promise<void> {
    let data: CachedSoundData;
    if (isCustomSound(soundId)) {
      data = await loadCustomSoundPeaks(soundId);
    } else {
      data = generateSynthesizedPeaks(soundId);
    }
    peaks = data.peaks;
    audioDuration = data.duration;
  }

  /** Find max absolute value in peaks array */
  function absMax(values: number[]): number {
    let max = 0;
    for (const v of values) {
      const abs = Math.abs(v);
      if (abs > max) max = abs;
    }
    return max || 1;
  }

  /** Calculate volume at a specific time point */
  function getVolumeAtTime(time: number): number {
    if (!audioSettings.ascendingVolume) return audioSettings.maxVolume / 100;

    const startVol = audioSettings.startVolume / 100;
    const maxVol = audioSettings.maxVolume / 100;

    if (time >= audioSettings.ascendDurationSeconds) return maxVol;

    const progress = time / audioSettings.ascendDurationSeconds;
    return startVol + (maxVol - startVol) * progress;
  }

  /** Draw waveform bars on canvas */
  function drawBars(
    ctx: CanvasRenderingContext2D,
    canvasWidth: number,
    canvasHeight: number,
    peakData: number[],
    fillColor: string
  ) {
    const gap = Math.max(pixelRatio, 1);
    const bar = barWidth * pixelRatio;
    const step = bar + gap;
    const halfH = canvasHeight / 2;
    const maxVal = absMax(peakData);
    const scale = peakData.length / canvasWidth;

    ctx.fillStyle = fillColor;

    for (let i = 0; i < canvasWidth; i += step) {
      const peakIndex = Math.floor(i * scale);

      // Calculate active volume scale for this point
      const timeAtPixel = (i / canvasWidth) * (audioDuration || 1);
      const vol = getVolumeAtTime(timeAtPixel);

      // Apply global mute/dimming if needed (e.g. valid 'volumeScale' context)
      // For now, we mix the 'shape' volume with the max height

      let h = Math.round((peakData[peakIndex] / maxVal) * halfH * vol);
      if (h === 0) h = 1;

      // Draw bar from center
      const x = i;
      const y = halfH - h;
      const barHeight = h * 2;

      ctx.beginPath();
      ctx.roundRect(x, y, bar, barHeight, 1);
      ctx.fill();
    }
  }

  /** Draw wave (smooth line) on canvas */
  function drawWave(
    ctx: CanvasRenderingContext2D,
    canvasWidth: number,
    canvasHeight: number,
    peakData: number[],
    fillColor: string
  ) {
    const halfH = canvasHeight / 2;
    const maxVal = absMax(peakData);
    const length = peakData.length;
    const scale = canvasWidth / length;

    ctx.fillStyle = fillColor;
    ctx.beginPath();
    ctx.moveTo(0, halfH);

    // Draw top half
    for (let i = 0; i < length; i++) {
      const x = i * scale;
      const timeAtPixel = (x / canvasWidth) * (audioDuration || 1);
      const vol = getVolumeAtTime(timeAtPixel);

      const h = Math.round((peakData[i] / maxVal) * halfH * vol);
      ctx.lineTo(x, halfH - h);
    }

    // Draw bottom half (mirror)
    for (let i = length - 1; i >= 0; i--) {
      const x = i * scale;
      const timeAtPixel = (x / canvasWidth) * (audioDuration || 1);
      const vol = getVolumeAtTime(timeAtPixel);

      const h = Math.round((peakData[i] / maxVal) * halfH * vol);
      ctx.lineTo(x, halfH + h);
    }

    ctx.closePath();
    ctx.fill();
  }

  /** Update canvas size and redraw */
  function updateCanvas() {
    if (!canvasEl || !progressCanvasEl || peaks.length === 0) return;

    const ctx = canvasEl.getContext("2d");
    const progressCtx = progressCanvasEl.getContext("2d");
    if (!ctx || !progressCtx) return;

    const canvasWidth = Math.round(width * pixelRatio);
    const canvasHeight = Math.round(height * pixelRatio);

    // Set canvas dimensions
    canvasEl.width = canvasWidth;
    canvasEl.height = canvasHeight;
    canvasEl.style.width = `${width}px`;
    canvasEl.style.height = `${height}px`;

    progressCanvasEl.width = canvasWidth;
    progressCanvasEl.height = canvasHeight;
    progressCanvasEl.style.width = `${width}px`;
    progressCanvasEl.style.height = `${height}px`;

    // Clear canvases
    ctx.clearRect(0, 0, canvasWidth, canvasHeight);
    progressCtx.clearRect(0, 0, canvasWidth, canvasHeight);

    // Draw background waveform
    // Draw background waveform
    if (barWidth > 0) {
      drawBars(ctx, canvasWidth, canvasHeight, peaks, color);
    } else {
      drawWave(ctx, canvasWidth, canvasHeight, peaks, color);
    }

    // Draw progress waveform (clipped)
    if (playing && progressPosition > 0) {
      const progressWidth = Math.round(canvasWidth * progressPosition);
      progressCtx.save();
      progressCtx.beginPath();
      progressCtx.rect(0, 0, progressWidth, canvasHeight);
      progressCtx.clip();

      if (barWidth > 0) {
        drawBars(progressCtx, canvasWidth, canvasHeight, peaks, progressColor);
      } else {
        drawWave(progressCtx, canvasWidth, canvasHeight, peaks, progressColor);
      }

      progressCtx.restore();
    }

    // Draw ascending volume envelope if enabled
    if (audioSettings.ascendingVolume) {
      drawVolumeEnvelope(ctx, canvasWidth, canvasHeight);
    }

    // Draw volume indicator
    drawVolumeIndicator(ctx, canvasWidth, canvasHeight);
  }

  /** Draw volume envelope overlay */
  function drawVolumeEnvelope(
    ctx: CanvasRenderingContext2D,
    canvasWidth: number,
    canvasHeight: number
  ) {
    const startVol = audioSettings.startVolume / 100;
    const maxVol = audioSettings.maxVolume / 100;
    const halfH = canvasHeight / 2;
    const maxAmplitude = halfH - 4 * pixelRatio;

    ctx.save();
    ctx.globalAlpha = 0.1;
    ctx.fillStyle = progressColor;

    ctx.beginPath();
    ctx.moveTo(0, halfH);

    for (let x = 0; x < canvasWidth; x++) {
      const timeAtPixel = (x / canvasWidth) * audioDuration;
      const progress = Math.min(
        timeAtPixel / audioSettings.ascendDurationSeconds,
        1
      );
      const vol = startVol + (maxVol - startVol) * progress;
      const amp = vol * maxAmplitude;
      ctx.lineTo(x, halfH - amp);
    }

    for (let x = canvasWidth - 1; x >= 0; x--) {
      const timeAtPixel = (x / canvasWidth) * audioDuration;
      const progress = Math.min(
        timeAtPixel / audioSettings.ascendDurationSeconds,
        1
      );
      const vol = startVol + (maxVol - startVol) * progress;
      const amp = vol * maxAmplitude;
      ctx.lineTo(x, halfH + amp);
    }

    ctx.closePath();
    ctx.fill();
    ctx.restore();

    // Draw progress line if playing
    if (playing && progressPosition > 0 && progressPosition < 1) {
      const progressX = progressPosition * canvasWidth;
      ctx.save();
      ctx.strokeStyle = progressColor;
      ctx.lineWidth = 2 * pixelRatio;
      ctx.setLineDash([4 * pixelRatio, 2 * pixelRatio]);
      ctx.beginPath();
      ctx.moveTo(progressX, 4 * pixelRatio);
      ctx.lineTo(progressX, canvasHeight - 4 * pixelRatio);
      ctx.stroke();
      ctx.restore();
    }
  }

  /** Draw volume percentage indicator */
  function drawVolumeIndicator(
    ctx: CanvasRenderingContext2D,
    canvasWidth: number,
    _canvasHeight: number
  ) {
    let currentVolume = audioSettings.maxVolume;
    if (playing && audioSettings.ascendingVolume) {
      const elapsed = (performance.now() - startTime) / 1000;
      const progress = Math.min(
        elapsed / audioSettings.ascendDurationSeconds,
        1
      );
      currentVolume =
        audioSettings.startVolume +
        (audioSettings.maxVolume - audioSettings.startVolume) * progress;
    } else if (!playing && audioSettings.ascendingVolume) {
      currentVolume = audioSettings.startVolume;
    }

    ctx.save();
    ctx.font = `${10 * pixelRatio}px system-ui, sans-serif`;
    ctx.fillStyle = color;
    ctx.textAlign = "right";
    ctx.fillText(
      `${Math.round(currentVolume)}%`,
      canvasWidth - 4 * pixelRatio,
      12 * pixelRatio
    );

    if (audioSettings.ascendingVolume && !playing) {
      ctx.textAlign = "left";
      ctx.fillText("↗ Ascending", 4 * pixelRatio, 12 * pixelRatio);
    }
    ctx.restore();
  }

  /** Animation loop for playing state */
  function animate() {
    if (!playing) return;

    const elapsed = (performance.now() - startTime) / 1000;
    const duration = audioSettings.ascendingVolume
      ? Math.max(audioSettings.ascendDurationSeconds, 3)
      : 3;

    progressPosition = Math.min(elapsed / duration, 1);

    if (progressPosition < 1) {
      animationFrame = requestAnimationFrame(animate);
    }
  }

  onMount(() => {
    pixelRatio = window.devicePixelRatio || 1;

    const unsubscribe = subscribeToPreviewState((state) => {
      previewState = state;
    });

    return () => {
      unsubscribe();
      if (animationFrame) {
        cancelAnimationFrame(animationFrame);
      }
    };
  });

  // Effect: Load peaks when sound ID changes
  $effect(() => {
    loadPeaks(audioSettings.soundId);
  });

  // Effect: Handle playing state
  $effect(() => {
    if (playing) {
      startTime = performance.now();
      progressPosition = 0;
      animate();
    } else {
      if (animationFrame) {
        cancelAnimationFrame(animationFrame);
        animationFrame = null;
      }
      progressPosition = 0;
    }
  });

  // Effect: Update canvas when visual dependencies change
  $effect(() => {
    updateCanvas();
  });

  // Handle resize
  function handleResize() {
    pixelRatio = window.devicePixelRatio || 1;
  }
</script>

<svelte:window onresize={handleResize} />

<div
  class="waveform-container relative h-(--wave-h) w-(--wave-w) rounded-md overflow-hidden border bg-muted"
  style:--wave-w="{width}px"
  style:--wave-h="{height}px"
>
  <canvas
    bind:this={canvasEl}
    class="waveform-canvas absolute top-0 left-0"
  ></canvas>
  <canvas
    bind:this={progressCanvasEl}
    class="progress-canvas absolute top-0 left-0 z-1"
  ></canvas>
</div>

<style>
  .waveform-container {
    display: block;
  }

  .waveform-canvas,
  .progress-canvas {
    display: block;
  }
</style>
