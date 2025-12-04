<script lang="ts">
  import { goto } from "$app/navigation";
  import { browser } from "$app/environment";
  import { page } from "$app/state";
  import * as Card from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Slider } from "$lib/components/ui/slider";
  import * as Select from "$lib/components/ui/select";
  import { Badge } from "$lib/components/ui/badge";
  import {
    Plus,
    Minus,
    ArrowLeft,
    Play,
    Save,
    RotateCcw,
    Trash2,
  } from "lucide-svelte";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";

  // Element type definitions
  type ElementType = "sg" | "dt" | "ar" | "ag" | "time" | "nl";

  interface ClockElement {
    id: string;
    type: ElementType;
    size: number;
  }

  const elementInfo: Record<
    ElementType,
    {
      name: string;
      description: string;
      defaultSize: number;
      minSize: number;
      maxSize: number;
    }
  > = {
    sg: {
      name: "Blood Glucose",
      description: "Current BG value",
      defaultSize: 40,
      minSize: 20,
      maxSize: 80,
    },
    dt: {
      name: "Delta",
      description: "Change since last reading",
      defaultSize: 14,
      minSize: 10,
      maxSize: 40,
    },
    ar: {
      name: "Arrow",
      description: "Trend direction arrow",
      defaultSize: 25,
      minSize: 15,
      maxSize: 50,
    },
    ag: {
      name: "Reading Age",
      description: "Time since last reading",
      defaultSize: 10,
      minSize: 6,
      maxSize: 24,
    },
    time: {
      name: "Current Time",
      description: "Display current time",
      defaultSize: 20,
      minSize: 12,
      maxSize: 48,
    },
    nl: {
      name: "Line Break",
      description: "New line separator",
      defaultSize: 0,
      minSize: 0,
      maxSize: 0,
    },
  };

  // Get preset face from URL params
  const presetFace = $derived(page.url.searchParams.get("face"));

  // Get realtime store for live preview
  const realtimeStore = getRealtimeStore();
  const currentBG = $derived(realtimeStore.currentBG);
  const bgDelta = $derived(realtimeStore.bgDelta);
  const direction = $derived(realtimeStore.direction);

  // Configuration state
  let bgColor = $state(false);
  let alwaysShowTime = $state(false);
  let staleMinutes = $state(13);

  // LIFO element stack - elements are added/removed from the end
  let elements = $state<ClockElement[]>([
    { id: crypto.randomUUID(), type: "sg", size: 40 },
    { id: crypto.randomUUID(), type: "dt", size: 14 },
    { id: crypto.randomUUID(), type: "nl", size: 0 },
    { id: crypto.randomUUID(), type: "ar", size: 25 },
    { id: crypto.randomUUID(), type: "nl", size: 0 },
    { id: crypto.randomUUID(), type: "ag", size: 10 },
  ]);

  // Element to add selector
  let selectedElementType = $state<ElementType>("sg");

  // Generate face string from current configuration
  const faceString = $derived.by(() => {
    const bgPrefix = bgColor ? "c" : "b";
    const timePrefix = alwaysShowTime ? "y" : "n";
    const staleStr = staleMinutes.toString().padStart(2, "0");

    let result = `${bgPrefix}${timePrefix}${staleStr}`;

    for (const element of elements) {
      if (element.type === "nl") {
        result += "-nl";
      } else {
        result += `-${element.type}${element.size}`;
      }
    }

    return result;
  });

  // Get direction arrow
  function getDirectionArrow(dir: string): string {
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
  }

  // Get BG color class based on value
  function getBgColorClass(bg: number): string {
    if (bg < 70) return "text-red-500";
    if (bg < 80) return "text-yellow-500";
    if (bg > 250) return "text-red-500";
    if (bg > 180) return "text-orange-500";
    return "text-green-500";
  }

  // Get BG background class
  function getBgBgClass(bg: number): string {
    if (bg < 70) return "bg-red-500";
    if (bg < 80) return "bg-yellow-500";
    if (bg > 250) return "bg-red-500";
    if (bg > 180) return "bg-orange-500";
    return "bg-green-500";
  }

  // Add element to the stack (LIFO - adds to end)
  function addElement() {
    const info = elementInfo[selectedElementType];
    elements = [
      ...elements,
      {
        id: crypto.randomUUID(),
        type: selectedElementType,
        size: info.defaultSize,
      },
    ];
  }

  // Remove last element (LIFO - removes from end)
  function removeLastElement() {
    if (elements.length > 0) {
      elements = elements.slice(0, -1);
    }
  }

  // Remove specific element by id
  function removeElement(id: string) {
    elements = elements.filter((e) => e.id !== id);
  }

  // Update element size
  function updateElementSize(id: string, size: number) {
    elements = elements.map((e) => (e.id === id ? { ...e, size } : e));
  }

  // Clear all elements
  function clearElements() {
    elements = [];
  }

  // Reset to default configuration
  function resetToDefault() {
    bgColor = false;
    alwaysShowTime = false;
    staleMinutes = 13;
    elements = [
      { id: crypto.randomUUID(), type: "sg", size: 40 },
      { id: crypto.randomUUID(), type: "dt", size: 14 },
      { id: crypto.randomUUID(), type: "nl", size: 0 },
      { id: crypto.randomUUID(), type: "ar", size: 25 },
      { id: crypto.randomUUID(), type: "nl", size: 0 },
      { id: crypto.randomUUID(), type: "ag", size: 10 },
    ];
  }

  // Save configuration to localStorage
  function saveConfiguration() {
    if (!browser) return;
    const config = { bgColor, alwaysShowTime, staleMinutes, elements };
    localStorage.setItem("clockConfig", JSON.stringify(config));
  }

  // Load configuration from localStorage
  function loadConfiguration() {
    if (!browser) return;
    const saved = localStorage.getItem("clockConfig");
    if (saved) {
      try {
        const config = JSON.parse(saved);
        bgColor = config.bgColor ?? false;
        alwaysShowTime = config.alwaysShowTime ?? false;
        staleMinutes = config.staleMinutes ?? 13;
        elements = config.elements ?? [];
      } catch (e) {
        console.error("Failed to load configuration:", e);
      }
    }
  }

  // Navigate to preview clock
  function openClock() {
    goto(`/clock/${faceString}`);
  }

  // Load configuration on mount
  $effect(() => {
    if (browser) {
      loadConfiguration();
    }
  });

  // Current time for preview
  let currentTime = $state(new Date());
  $effect(() => {
    if (!browser) return;
    const interval = setInterval(() => {
      currentTime = new Date();
    }, 1000);
    return () => clearInterval(interval);
  });

  const formattedTime = $derived(
    currentTime.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
  );

  // Derived classes for preview
  const textColorClass = $derived(
    bgColor ? "text-white" : getBgColorClass(currentBG)
  );
  const previewBgClass = $derived(
    bgColor ? getBgBgClass(currentBG) : "bg-neutral-950"
  );
</script>

<svelte:head>
  <title>Clock Configuration - Nightscout</title>
  <meta
    name="description"
    content="Configure your Nightscout clock display settings"
  />
</svelte:head>

<div class="min-h-dvh bg-background p-4 text-foreground sm:p-6 md:p-8">
  <div class="mx-auto max-w-6xl">
    <!-- Header -->
    <div class="mb-6 flex items-center justify-between">
      <Button variant="ghost" href="/clock" class="gap-2">
        <ArrowLeft class="size-4" />
        Back to Clocks
      </Button>
      <h1 class="text-2xl font-bold text-primary sm:text-3xl">Clock Builder</h1>
      <div class="w-24"></div>
    </div>

    <div class="grid grid-cols-1 gap-6 lg:grid-cols-2">
      <!-- Left Column: Live Preview -->
      <div class="space-y-4">
        <Card.Root>
          <Card.Header class="pb-2">
            <Card.Title class="text-lg">Live Preview</Card.Title>
          </Card.Header>
          <Card.Content>
            <!-- Live Preview Display -->
            <div
              class="relative flex min-h-[300px] flex-col items-center justify-center overflow-hidden rounded-lg transition-colors duration-300 {previewBgClass}"
            >
              <div class="flex flex-col items-center gap-1 p-4">
                {#each elements as element (element.id)}
                  {#if element.type === "nl"}
                    <div class="h-2 w-full"></div>
                  {:else if element.type === "sg"}
                    <span
                      class="font-bold tabular-nums leading-none {textColorClass}"
                      style:font-size="{element.size * 0.8}px"
                    >
                      {currentBG}
                    </span>
                  {:else if element.type === "dt"}
                    <span
                      class="font-medium tabular-nums {textColorClass}"
                      style:font-size="{element.size * 0.8}px"
                    >
                      {bgDelta > 0 ? "+" : ""}{bgDelta} mg/dL
                    </span>
                  {:else if element.type === "ar"}
                    <span
                      class="leading-none {textColorClass}"
                      style:font-size="{element.size * 0.8}px"
                    >
                      {getDirectionArrow(direction)}
                    </span>
                  {:else if element.type === "ag"}
                    <span
                      class="font-medium opacity-70 {textColorClass}"
                      style:font-size="{element.size * 0.8}px"
                    >
                      3m ago
                    </span>
                  {:else if element.type === "time"}
                    <span
                      class="font-medium tabular-nums opacity-80 {textColorClass}"
                      style:font-size="{element.size * 0.8}px"
                    >
                      {formattedTime}
                    </span>
                  {/if}
                {/each}
              </div>
            </div>

            <!-- Generated Face String -->
            <div class="mt-4 rounded-md bg-muted p-3">
              <div class="mb-1 text-xs text-muted-foreground">
                Generated Face String:
              </div>
              <code class="break-all font-mono text-sm text-primary">
                {faceString}
              </code>
            </div>

            <!-- Action Buttons -->
            <div class="mt-4 flex flex-wrap gap-2">
              <Button onclick={openClock} class="flex-1 gap-2">
                <Play class="size-4" />
                Open Clock
              </Button>
              <Button
                variant="secondary"
                onclick={saveConfiguration}
                class="gap-2"
              >
                <Save class="size-4" />
                Save
              </Button>
              <Button variant="outline" onclick={resetToDefault} class="gap-2">
                <RotateCcw class="size-4" />
                Reset
              </Button>
            </div>
          </Card.Content>
        </Card.Root>

        <!-- General Settings -->
        <Card.Root>
          <Card.Header class="pb-2">
            <Card.Title class="text-lg">Display Settings</Card.Title>
          </Card.Header>
          <Card.Content class="space-y-4">
            <div class="flex items-center justify-between">
              <div>
                <Label for="bgColor">Colorful Background</Label>
                <p class="text-xs text-muted-foreground">
                  Use BG-based colored background
                </p>
              </div>
              <Checkbox id="bgColor" bind:checked={bgColor} />
            </div>

            <div class="flex items-center justify-between">
              <div>
                <Label for="alwaysShowTime">Always Show Reading Age</Label>
                <p class="text-xs text-muted-foreground">
                  Always display when last reading was taken
                </p>
              </div>
              <Checkbox id="alwaysShowTime" bind:checked={alwaysShowTime} />
            </div>

            <div class="space-y-2">
              <Label for="staleMinutes">Stale Threshold (minutes)</Label>
              <div class="flex items-center gap-2">
                <Input
                  id="staleMinutes"
                  type="number"
                  min="0"
                  max="60"
                  bind:value={staleMinutes}
                  class="w-20"
                />
                <span class="text-sm text-muted-foreground">
                  (0 = never stale)
                </span>
              </div>
            </div>
          </Card.Content>
        </Card.Root>
      </div>

      <!-- Right Column: Element Builder -->
      <div class="space-y-4">
        <!-- Add Element Controls -->
        <Card.Root>
          <Card.Header class="pb-2">
            <Card.Title class="text-lg">Add Element</Card.Title>
            <Card.Description>
              Elements are added to the end of the display (LIFO style)
            </Card.Description>
          </Card.Header>
          <Card.Content>
            <div class="flex gap-2">
              <Select.Root
                type="single"
                value={selectedElementType}
                onValueChange={(v) => {
                  if (v) selectedElementType = v as ElementType;
                }}
              >
                <Select.Trigger class="flex-1">
                  {elementInfo[selectedElementType].name}
                </Select.Trigger>
                <Select.Content>
                  {#each Object.entries(elementInfo) as [type, info]}
                    <Select.Item value={type}>
                      <div class="flex flex-col items-start">
                        <span>{info.name}</span>
                        <span class="text-xs text-muted-foreground">
                          {info.description}
                        </span>
                      </div>
                    </Select.Item>
                  {/each}
                </Select.Content>
              </Select.Root>
              <Button onclick={addElement} class="gap-2">
                <Plus class="size-4" />
                Add
              </Button>
            </div>

            <div class="mt-3 flex gap-2">
              <Button
                variant="outline"
                onclick={removeLastElement}
                disabled={elements.length === 0}
                class="flex-1 gap-2"
              >
                <Minus class="size-4" />
                Remove Last
              </Button>
              <Button
                variant="destructive"
                onclick={clearElements}
                disabled={elements.length === 0}
                class="gap-2"
              >
                <Trash2 class="size-4" />
                Clear All
              </Button>
            </div>
          </Card.Content>
        </Card.Root>

        <!-- Element Stack -->
        <Card.Root>
          <Card.Header class="pb-2">
            <Card.Title class="text-lg">
              Element Stack
              <Badge variant="secondary" class="ml-2">{elements.length}</Badge>
            </Card.Title>
            <Card.Description>
              Elements display from top to bottom
            </Card.Description>
          </Card.Header>
          <Card.Content>
            {#if elements.length === 0}
              <div
                class="rounded-md border border-dashed p-8 text-center text-muted-foreground"
              >
                No elements added yet. Add elements above to build your clock
                face.
              </div>
            {:else}
              <div class="space-y-2">
                {#each elements as element, index (element.id)}
                  <div
                    class="flex items-center gap-2 rounded-md border bg-card p-2"
                  >
                    <div
                      class="flex size-6 items-center justify-center text-xs text-muted-foreground"
                    >
                      {index + 1}
                    </div>

                    {#if element.type === "nl"}
                      <div class="flex-1">
                        <div class="flex items-center gap-2">
                          <Badge variant="outline">Line Break</Badge>
                          <span class="text-xs text-muted-foreground">
                            New row separator
                          </span>
                        </div>
                      </div>
                    {:else}
                      <div class="flex-1">
                        <div class="flex items-center gap-2">
                          <Badge>{elementInfo[element.type].name}</Badge>
                          <code class="text-xs text-muted-foreground">
                            {element.type}{element.size}
                          </code>
                        </div>
                        <div class="mt-2 flex items-center gap-2">
                          <span class="text-xs text-muted-foreground w-12">
                            Size: {element.size}
                          </span>
                          <Slider
                            type="single"
                            value={element.size}
                            onValueChange={(v) =>
                              updateElementSize(element.id, v)}
                            min={elementInfo[element.type].minSize}
                            max={elementInfo[element.type].maxSize}
                            step={1}
                            class="flex-1"
                          />
                        </div>
                      </div>
                    {/if}

                    <Button
                      variant="ghost"
                      size="icon"
                      onclick={() => removeElement(element.id)}
                      class="size-8 text-muted-foreground hover:text-destructive"
                    >
                      <Trash2 class="size-4" />
                    </Button>
                  </div>
                {/each}
              </div>
            {/if}
          </Card.Content>
        </Card.Root>

        <!-- Quick Presets -->
        <Card.Root>
          <Card.Header class="pb-2">
            <Card.Title class="text-lg">Quick Presets</Card.Title>
          </Card.Header>
          <Card.Content>
            <div class="grid grid-cols-2 gap-2">
              <Button
                variant="outline"
                onclick={() => {
                  bgColor = false;
                  alwaysShowTime = false;
                  staleMinutes = 0;
                  elements = [
                    { id: crypto.randomUUID(), type: "sg", size: 60 },
                  ];
                }}
              >
                Simple BG
              </Button>
              <Button
                variant="outline"
                onclick={() => {
                  bgColor = true;
                  alwaysShowTime = false;
                  staleMinutes = 13;
                  elements = [
                    { id: crypto.randomUUID(), type: "sg", size: 35 },
                    { id: crypto.randomUUID(), type: "dt", size: 14 },
                    { id: crypto.randomUUID(), type: "nl", size: 0 },
                    { id: crypto.randomUUID(), type: "ar", size: 25 },
                    { id: crypto.randomUUID(), type: "nl", size: 0 },
                    { id: crypto.randomUUID(), type: "ag", size: 6 },
                  ];
                }}
              >
                Color Detailed
              </Button>
              <Button
                variant="outline"
                onclick={() => {
                  bgColor = false;
                  alwaysShowTime = false;
                  staleMinutes = 0;
                  elements = [
                    { id: crypto.randomUUID(), type: "sg", size: 80 },
                    { id: crypto.randomUUID(), type: "nl", size: 0 },
                    { id: crypto.randomUUID(), type: "time", size: 30 },
                  ];
                }}
              >
                Large + Time
              </Button>
              <Button
                variant="outline"
                onclick={() => {
                  bgColor = false;
                  alwaysShowTime = false;
                  staleMinutes = 13;
                  elements = [
                    { id: crypto.randomUUID(), type: "sg", size: 50 },
                    { id: crypto.randomUUID(), type: "ar", size: 30 },
                  ];
                }}
              >
                BG + Arrow
              </Button>
            </div>
          </Card.Content>
        </Card.Root>
      </div>
    </div>
  </div>
</div>
