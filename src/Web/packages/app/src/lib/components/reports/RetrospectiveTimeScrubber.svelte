<script lang="ts">
  import { Slider } from "$lib/components/ui/slider";
  import { Button } from "$lib/components/ui/button";
  import { Play, Pause, SkipBack, SkipForward } from "lucide-svelte";
  import { onDestroy } from "svelte";
  import { time } from "$lib/utils/formatting";

  interface Props {
    /** The date being reviewed */
    date: Date;
    /** Current scrub time (bindable) */
    currentTime?: Date;
    /** Callback when time changes */
    onTimeChange?: (time: Date) => void;
    /** Step size in minutes for keyboard/button navigation */
    stepMinutes?: number;
    /** Playback speed multiplier */
    playbackSpeed?: number;
  }

  let {
    date,
    currentTime = $bindable(new Date()),
    onTimeChange,
    stepMinutes = 5,
    playbackSpeed = 60, // 1 second = 1 minute of playback
  }: Props = $props();

  // Total minutes in the day (for slider range)
  const totalMinutes = 24 * 60; // 1440 minutes

  // Convert current time to slider value (minutes from midnight)
  let sliderValue = $state(0);

  // Sync slider value with currentTime
  $effect(() => {
    if (currentTime) {
      const minutes = currentTime.getHours() * 60 + currentTime.getMinutes();
      sliderValue = minutes;
    }
  });

  // Playback state
  let isPlaying = $state(false);
  let playbackInterval: ReturnType<typeof setInterval> | null = null;

  // Convert slider value back to time
  function sliderToTime(minutes: number): Date {
    const hours = Math.floor(minutes / 60);
    const mins = minutes % 60;
    return new Date(
      date.getFullYear(),
      date.getMonth(),
      date.getDate(),
      hours,
      mins,
      0
    );
  }

  // Handle slider change
  function handleSliderChange(value: number) {
    const newTime = sliderToTime(value);
    currentTime = newTime;
    onTimeChange?.(newTime);
  }

  // Format time label for slider
  function formatSliderLabel(minutes: number): string {
    const hours = Math.floor(minutes / 60);
    const mins = minutes % 60;
    const period = hours >= 12 ? "PM" : "AM";
    const displayHour = hours === 0 ? 12 : hours > 12 ? hours - 12 : hours;
    return `${displayHour}:${mins.toString().padStart(2, "0")} ${period}`;
  }

  // Step navigation
  function stepBack() {
    const newMinutes = Math.max(0, sliderValue - stepMinutes);
    handleSliderChange(newMinutes);
  }

  function stepForward() {
    const newMinutes = Math.min(totalMinutes - 1, sliderValue + stepMinutes);
    handleSliderChange(newMinutes);
  }

  // Go to start of day
  function goToStart() {
    handleSliderChange(0);
  }

  // Playback controls
  function togglePlayback() {
    if (isPlaying) {
      stopPlayback();
    } else {
      startPlayback();
    }
  }

  function startPlayback() {
    isPlaying = true;
    // Advance 1 minute every (1000 / playbackSpeed) ms
    const intervalMs = (1000 / playbackSpeed) * 60;
    playbackInterval = setInterval(
      () => {
        const newMinutes = sliderValue + 1;
        if (newMinutes >= totalMinutes) {
          stopPlayback();
          return;
        }
        handleSliderChange(newMinutes);
      },
      Math.max(16, intervalMs)
    ); // Cap at ~60fps
  }

  function stopPlayback() {
    isPlaying = false;
    if (playbackInterval) {
      clearInterval(playbackInterval);
      playbackInterval = null;
    }
  }

  // Handle keyboard navigation
  function handleKeydown(event: KeyboardEvent) {
    if (event.key === "ArrowLeft") {
      event.preventDefault();
      stepBack();
    } else if (event.key === "ArrowRight") {
      event.preventDefault();
      stepForward();
    } else if (event.key === " ") {
      event.preventDefault();
      togglePlayback();
    } else if (event.key === "Home") {
      event.preventDefault();
      goToStart();
    }
  }

  // Cleanup on destroy
  onDestroy(() => {
    stopPlayback();
  });
</script>

<svelte:window onkeydown={handleKeydown} />

<div
  class="flex flex-col gap-3 p-4 rounded-lg border bg-card"
  role="group"
  aria-label="Time scrubber - use arrow keys to navigate, space to play/pause"
>
  <!-- Time Display -->
  <div class="flex items-center justify-between">
    <div class="text-sm text-muted-foreground">
      {formatSliderLabel(0)}
    </div>
    <div class="text-center">
      <div class="text-2xl font-bold tabular-nums">
        {time(currentTime)}
      </div>
      <div class="text-xs text-muted-foreground">
        Scrub time to see data at that moment
      </div>
    </div>
    <div class="text-sm text-muted-foreground">
      {formatSliderLabel(totalMinutes - 1)}
    </div>
  </div>

  <!-- Slider -->
  <div class="px-2">
    <Slider
      type="single"
      value={sliderValue}
      min={0}
      max={totalMinutes - 1}
      step={1}
      onValueChange={handleSliderChange}
      class="w-full"
    />
  </div>

  <!-- Playback Controls -->
  <div class="flex items-center justify-center gap-2">
    <Button
      variant="outline"
      size="icon"
      onclick={goToStart}
      title="Go to start (Home)"
    >
      <SkipBack class="h-4 w-4" />
    </Button>
    <Button
      variant="outline"
      size="icon"
      onclick={stepBack}
      title="Step back {stepMinutes} minutes (←)"
    >
      <span class="text-xs font-medium">-{stepMinutes}m</span>
    </Button>
    <Button
      variant={isPlaying ? "default" : "outline"}
      size="icon"
      onclick={togglePlayback}
      title={isPlaying ? "Pause (Space)" : "Play (Space)"}
    >
      {#if isPlaying}
        <Pause class="h-4 w-4" />
      {:else}
        <Play class="h-4 w-4" />
      {/if}
    </Button>
    <Button
      variant="outline"
      size="icon"
      onclick={stepForward}
      title="Step forward {stepMinutes} minutes (→)"
    >
      <span class="text-xs font-medium">+{stepMinutes}m</span>
    </Button>
    <Button
      variant="outline"
      size="icon"
      onclick={() => handleSliderChange(totalMinutes - 1)}
      title="Go to end"
    >
      <SkipForward class="h-4 w-4" />
    </Button>
  </div>

  <!-- Time markers -->
  <div class="flex justify-between text-xs text-muted-foreground px-2">
    {#each [0, 6, 12, 18, 24] as hour}
      <span>
        {hour === 0
          ? "12a"
          : hour === 12
            ? "12p"
            : hour > 12
              ? `${hour - 12}p`
              : `${hour}a`}
      </span>
    {/each}
  </div>
</div>
