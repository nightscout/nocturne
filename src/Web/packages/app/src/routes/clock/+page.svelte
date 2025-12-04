<script lang="ts">
  import { goto } from "$app/navigation";
  import { browser } from "$app/environment";
  import { page } from "$app/state";
  import * as Card from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Badge } from "$lib/components/ui/badge";
  import {
    Settings,
    Clock as ClockIcon,
    ExternalLink,
    Wrench,
    Sparkles,
  } from "lucide-svelte";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { parseClockFace } from "$lib/clock-parser";

  // Get face from URL search params
  const face = $derived(page.url.searchParams.get("face"));

  const realtimeStore = getRealtimeStore();
  const currentBG = $derived(realtimeStore.currentBG);
  const direction = $derived(realtimeStore.direction);

  // Get direction arrow
  const getDirectionArrow = (dir: string): string => {
    switch (dir) {
      case "DoubleUp":
        return "⇈";
      case "SingleUp":
        return "↑";
      case "FortyFiveUp":
        return "↗";
      case "Flat":
        return "→";
      case "FortyFiveDown":
        return "↘";
      case "SingleDown":
        return "↓";
      case "DoubleDown":
        return "⇊";
      default:
        return "→";
    }
  };

  // Get BG color class based on value
  const getBgColorClass = (bg: number): string => {
    if (bg < 70) return "text-red-500";
    if (bg < 80) return "text-yellow-500";
    if (bg > 250) return "text-red-500";
    if (bg > 180) return "text-orange-500";
    return "text-green-500";
  };

  // Get BG background class based on value
  const getBgBgClass = (bg: number): string => {
    if (bg < 70) return "bg-red-500";
    if (bg < 80) return "bg-yellow-500";
    if (bg > 250) return "bg-red-500";
    if (bg > 180) return "bg-orange-500";
    return "bg-green-500";
  };

  // Predefined clock faces with previews
  const clockFaces = [
    { id: "clock", name: "Simple Clock", description: "Basic clock display" },
    {
      id: "bgclock",
      name: "BG Clock",
      description: "Clock with blood glucose data",
    },
    {
      id: "clock-color",
      name: "Color Clock",
      description: "Colorful clock display",
    },
    {
      id: "bn0-sg40",
      name: "Large BG",
      description: "Large blood glucose display",
    },
    {
      id: "cy13-sg35-dt14-nl-ar25-nl-ag6",
      name: "Detailed View",
      description: "Detailed clock with all elements",
    },
    {
      id: "simple",
      name: "Simple Display",
      description: "Minimal blood glucose display",
    },
    {
      id: "large",
      name: "Extra Large",
      description: "Very large display with time",
    },
  ];

  // State using runes
  let customFace = $state("");

  // Load saved custom configuration if it exists
  let savedConfig = $state<string | null>(null);
  $effect(() => {
    if (browser) {
      const saved = localStorage.getItem("clockConfig");
      if (saved) {
        try {
          const config = JSON.parse(saved);
          // Reconstruct the face string from saved config
          const bgPrefix = config.bgColor ? "c" : "b";
          const timePrefix = config.alwaysShowTime ? "y" : "n";
          const staleStr = (config.staleMinutes || 13)
            .toString()
            .padStart(2, "0");
          let result = `${bgPrefix}${timePrefix}${staleStr}`;
          for (const element of config.elements || []) {
            if (element.type === "nl") {
              result += "-nl";
            } else {
              result += `-${element.type}${element.size}`;
            }
          }
          savedConfig = result;
        } catch (e) {
          savedConfig = null;
        }
      }
    }
  });

  function navigateToFace(faceId: string) {
    goto(`/clock/${encodeURIComponent(faceId)}`);
  }

  // If we have a face parameter, redirect to the proper route
  $effect(() => {
    if (browser && face) {
      goto(`/clock/${encodeURIComponent(face)}`);
    }
  });
</script>

<svelte:head>
  <title>Nightscout Clock{face ? ` - ${face}` : " Faces"}</title>
</svelte:head>

{#if face}
  <!-- Redirecting to clock display -->
  <div class="flex h-dvh items-center justify-center bg-background">
    <div class="text-center">
      <ClockIcon class="mx-auto mb-4 size-12 animate-pulse text-primary" />
      <p class="text-muted-foreground">Loading clock face...</p>
    </div>
  </div>
{:else}
  <!-- Clock Selection Mode -->
  <div
    class="min-h-dvh overflow-y-auto bg-background p-4 text-foreground sm:p-6 md:p-8"
  >
    <div class="mx-auto max-w-6xl">
      <h1
        class="mb-4 text-center text-2xl font-bold text-primary sm:text-3xl md:text-4xl"
      >
        Nightscout Clock Faces
      </h1>
      <p
        class="mb-6 text-center text-sm text-muted-foreground sm:mb-8 sm:text-base"
      >
        Choose a clock face to display your glucose data:
      </p>

      <!-- Custom Clock Builder - Featured First -->
      <Card.Root
        class="mb-6 border-2 border-primary/20 bg-linear-to-br from-primary/5 to-transparent sm:mb-8"
      >
        <Card.Content class="p-4 sm:p-6">
          <div
            class="flex flex-col gap-4 md:flex-row md:items-center md:justify-between"
          >
            <div class="flex items-start gap-4">
              <div
                class="flex size-12 shrink-0 items-center justify-center rounded-lg bg-primary/10"
              >
                <Wrench class="size-6 text-primary" />
              </div>
              <div>
                <div class="flex items-center gap-2">
                  <Card.Title class="text-xl font-bold sm:text-2xl">
                    Clock Builder
                  </Card.Title>
                  <Badge variant="secondary" class="gap-1">
                    <Sparkles class="size-3" />
                    Custom
                  </Badge>
                </div>
                <Card.Description class="mt-1 text-sm sm:text-base">
                  Build your own custom clock face with a visual editor. Add
                  elements, adjust sizes, and see a live preview as you design.
                </Card.Description>
              </div>
            </div>
            <div
              class="flex flex-col gap-2 sm:flex-row md:flex-col lg:flex-row"
            >
              <Button href="/clock/config" class="gap-2">
                <Settings class="size-4" />
                Open Builder
              </Button>
              {#if savedConfig}
                <Button
                  variant="secondary"
                  onclick={() => savedConfig && navigateToFace(savedConfig)}
                  class="gap-2"
                >
                  <ExternalLink class="size-4" />
                  Open Saved Clock
                </Button>
              {/if}
            </div>
          </div>

          <!-- Quick Custom Input -->
          <div class="mt-4 border-t pt-4">
            <div
              class="flex flex-col items-stretch gap-2 sm:flex-row sm:items-center"
            >
              <span class="text-sm text-muted-foreground">Quick open:</span>
              <Input
                type="text"
                placeholder="Enter face string (e.g., bn0-sg40-ar25)"
                bind:value={customFace}
                onkeydown={(e) =>
                  e.key === "Enter" && customFace && navigateToFace(customFace)}
                class="flex-1 font-mono text-sm"
              />
              <Button
                variant="outline"
                onclick={() => navigateToFace(customFace)}
                disabled={!customFace}
                class="shrink-0"
              >
                Go
              </Button>
            </div>
          </div>
        </Card.Content>
      </Card.Root>

      <!-- Preset Clock Face Grid with Live Previews -->
      <h2 class="mb-4 text-lg font-semibold text-muted-foreground">
        Preset Clock Faces
      </h2>
      <div
        class="grid grid-cols-1 gap-4 sm:grid-cols-2 sm:gap-6 lg:grid-cols-3"
      >
        {#each clockFaces as face}
          {@const config = parseClockFace(face.id)}
          {@const textColorClass = config.bgColor
            ? "text-white"
            : getBgColorClass(currentBG)}
          {@const bgClass = config.bgColor
            ? getBgBgClass(currentBG)
            : "bg-neutral-950"}

          <Card.Root
            class="cursor-pointer overflow-hidden transition-all duration-200 hover:-translate-y-1 hover:shadow-lg"
          >
            <!-- Live Preview -->
            <div
              class="relative flex h-28 items-center justify-center sm:h-32 {bgClass}"
            >
              <div class="flex items-center gap-2">
                {#each config.elements.filter((e) => e.type !== "nl") as element}
                  {#if element.type === "sg"}
                    <span
                      class="font-bold tabular-nums {textColorClass}"
                      style:font-size="{Math.min(
                        (element.size || 40) * 0.6,
                        48
                      )}px"
                    >
                      {currentBG}
                    </span>
                  {:else if element.type === "ar"}
                    <span
                      class={textColorClass}
                      style:font-size="{Math.min(
                        (element.size || 25) * 0.6,
                        32
                      )}px"
                    >
                      {getDirectionArrow(direction)}
                    </span>
                  {/if}
                {/each}
              </div>
            </div>

            <Card.Content class="p-3 sm:p-4">
              <Card.Title
                class="mb-1 text-base font-semibold text-primary sm:text-lg"
              >
                {face.name}
              </Card.Title>
              <Card.Description class="mb-2 text-xs sm:mb-3 sm:text-sm">
                {face.description}
              </Card.Description>
              <div
                class="mb-3 truncate rounded bg-muted px-2 py-1 font-mono text-xs text-muted-foreground sm:mb-4"
              >
                {face.id}
              </div>
              <Button
                onclick={() => navigateToFace(face.id)}
                class="w-full gap-2"
              >
                <ExternalLink class="size-4" />
                Open Clock
              </Button>
            </Card.Content>
          </Card.Root>
        {/each}
      </div>
    </div>
  </div>
{/if}
