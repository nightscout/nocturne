<script lang="ts">
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
    CardDescription,
  } from "$lib/components/ui/card";
  import { Label } from "$lib/components/ui/label";
  import { Input } from "$lib/components/ui/input";
  import { Separator } from "$lib/components/ui/separator";
  import * as Select from "$lib/components/ui/select";
  import * as ToggleGroup from "$lib/components/ui/toggle-group";
  import { Settings2 } from "lucide-svelte";
  import {
    HaloDialColorMode,
    HaloDialPredictionCurve,
    HaloDialCenterSubElement,
    HaloDialArcElement,
    HaloDialCornerElement,
    type HaloDialConfig,
  } from "$lib/components/dashboard/halo-dial/config";

  interface Props {
    value: HaloDialConfig;
    onchange: (config: HaloDialConfig) => void;
  }

  let { value, onchange }: Props = $props();

  const DISPLAY_OVERRIDES: Record<string, string> = {
    Iob: "IOB",
    Cob: "COB",
    Uam: "UAM",
  };

  function humanize(s: string): string {
    return DISPLAY_OVERRIDES[s] ?? s.replace(/([a-z])([A-Z])/g, "$1 $2");
  }

  function update(patch: Partial<HaloDialConfig>) {
    onchange({ ...value, ...patch });
  }

  function updateCorner(
    key: "tl" | "tr" | "bl" | "br",
    elements: string[],
  ) {
    onchange({
      ...value,
      corners: {
        ...value.corners,
        [key]: elements as HaloDialCornerElement[],
      },
    });
  }

  const historyOptions = [5, 10, 15, 30, 45, 60];
  const predictionOptions = [0, 15, 30, 45, 60, 90, 120];

  const arcOptions = Object.values(HaloDialArcElement);
  const cornerElements = Object.values(HaloDialCornerElement);

  let showIobMax = $derived(
    value.innerLeftArc === HaloDialArcElement.Iob ||
      value.innerRightArc === HaloDialArcElement.Iob,
  );

  let showCobMax = $derived(
    value.innerLeftArc === HaloDialArcElement.Cob ||
      value.innerRightArc === HaloDialArcElement.Cob,
  );
</script>

<Card>
  <CardHeader>
    <CardTitle class="flex items-center gap-2">
      <Settings2 class="h-5 w-5" />
      Halo Dial
    </CardTitle>
    <CardDescription>Configure the halo dial display</CardDescription>
  </CardHeader>
  <CardContent class="space-y-4">
    <!-- Section 1: Ring Settings -->
    <div class="grid gap-4 sm:grid-cols-2">
      <div class="space-y-2">
        <Label>Color mode</Label>
        <Select.Root
          type="single"
          value={value.colorMode}
          onValueChange={(v) => update({ colorMode: v as HaloDialColorMode })}
        >
          <Select.Trigger>
            {humanize(value.colorMode ?? "")}
          </Select.Trigger>
          <Select.Content>
            {#each Object.values(HaloDialColorMode) as mode (mode)}
              <Select.Item value={mode} label={humanize(mode)} />
            {/each}
          </Select.Content>
        </Select.Root>
      </div>

      <div class="space-y-2">
        <Label>History minutes</Label>
        <Select.Root
          type="single"
          value={String(value.historyMinutes)}
          onValueChange={(v) => update({ historyMinutes: Number(v) })}
        >
          <Select.Trigger>
            {value.historyMinutes}
          </Select.Trigger>
          <Select.Content>
            {#each historyOptions as mins (mins)}
              <Select.Item value={String(mins)} label={String(mins)} />
            {/each}
          </Select.Content>
        </Select.Root>
      </div>

      <div class="space-y-2">
        <Label>Prediction minutes</Label>
        <Select.Root
          type="single"
          value={String(value.predictionMinutes)}
          onValueChange={(v) => update({ predictionMinutes: Number(v) })}
        >
          <Select.Trigger>
            {value.predictionMinutes}
          </Select.Trigger>
          <Select.Content>
            {#each predictionOptions as mins (mins)}
              <Select.Item value={String(mins)} label={String(mins)} />
            {/each}
          </Select.Content>
        </Select.Root>
      </div>

      <div class="space-y-2">
        <Label>Prediction curve</Label>
        <Select.Root
          type="single"
          value={value.predictionCurve}
          onValueChange={(v) =>
            update({ predictionCurve: v as HaloDialPredictionCurve })}
        >
          <Select.Trigger>
            {humanize(value.predictionCurve ?? "")}
          </Select.Trigger>
          <Select.Content>
            {#each Object.values(HaloDialPredictionCurve) as curve (curve)}
              <Select.Item value={curve} label={humanize(curve)} />
            {/each}
          </Select.Content>
        </Select.Root>
      </div>
    </div>

    <Separator />

    <!-- Section 2: Center & Arcs -->
    <div class="grid gap-4 sm:grid-cols-2">
      <div class="space-y-2">
        <Label>Center sub-element</Label>
        <Select.Root
          type="single"
          value={value.centerSub}
          onValueChange={(v) =>
            update({ centerSub: v as HaloDialCenterSubElement })}
        >
          <Select.Trigger>
            {humanize(value.centerSub ?? "")}
          </Select.Trigger>
          <Select.Content>
            {#each Object.values(HaloDialCenterSubElement) as el (el)}
              <Select.Item value={el} label={humanize(el)} />
            {/each}
          </Select.Content>
        </Select.Root>
      </div>

      <div class="space-y-2">
        <Label>Left arc</Label>
        <Select.Root
          type="single"
          value={value.innerLeftArc ?? "none"}
          onValueChange={(v) =>
            update({
              innerLeftArc:
                v === "none" ? undefined : (v as HaloDialArcElement),
            })}
        >
          <Select.Trigger>
            {value.innerLeftArc ? humanize(value.innerLeftArc) : "None"}
          </Select.Trigger>
          <Select.Content>
            <Select.Item value="none" label="None" />
            {#each arcOptions as arc (arc)}
              <Select.Item value={arc} label={humanize(arc)} />
            {/each}
          </Select.Content>
        </Select.Root>
      </div>

      <div class="space-y-2">
        <Label>Right arc</Label>
        <Select.Root
          type="single"
          value={value.innerRightArc ?? "none"}
          onValueChange={(v) =>
            update({
              innerRightArc:
                v === "none" ? undefined : (v as HaloDialArcElement),
            })}
        >
          <Select.Trigger>
            {value.innerRightArc ? humanize(value.innerRightArc) : "None"}
          </Select.Trigger>
          <Select.Content>
            <Select.Item value="none" label="None" />
            {#each arcOptions as arc (arc)}
              <Select.Item value={arc} label={humanize(arc)} />
            {/each}
          </Select.Content>
        </Select.Root>
      </div>

      {#if showIobMax}
        <div class="space-y-2">
          <Label>IOB max units</Label>
          <Input
            type="number"
            min={1}
            step={0.5}
            value={value.iobMaxUnits}
            oninput={(e) => {
              const n = Number((e.currentTarget as HTMLInputElement).value);
              if (n > 0) update({ iobMaxUnits: n });
            }}
          />
        </div>
      {/if}

      {#if showCobMax}
        <div class="space-y-2">
          <Label>COB max grams</Label>
          <Input
            type="number"
            min={1}
            step={5}
            value={value.cobMaxGrams}
            oninput={(e) => {
              const n = Number((e.currentTarget as HTMLInputElement).value);
              if (n > 0) update({ cobMaxGrams: n });
            }}
          />
        </div>
      {/if}
    </div>

    <Separator />

    <!-- Section 3: Corners -->
    <div class="space-y-4">
      {#each [
        { key: "tl", label: "Top Left" },
        { key: "tr", label: "Top Right" },
        { key: "bl", label: "Bottom Left" },
        { key: "br", label: "Bottom Right" },
      ] as corner (corner.key)}
        {@const cornerKey = corner.key as "tl" | "tr" | "bl" | "br"}
        <div class="space-y-2">
          <Label>{corner.label}</Label>
          <ToggleGroup.Root
            type="multiple"
            size="sm"
            value={(value.corners?.[cornerKey] ?? []) as string[]}
            onValueChange={(v) => updateCorner(cornerKey, v)}
          >
            {#each cornerElements as el (el)}
              <ToggleGroup.Item
                value={el}
                aria-label={humanize(el)}
                class="h-7 px-2 text-xs"
              >
                {humanize(el)}
              </ToggleGroup.Item>
            {/each}
          </ToggleGroup.Root>
        </div>
      {/each}
    </div>

    <p class="text-xs text-muted-foreground">
      Changes take effect immediately
    </p>
  </CardContent>
</Card>
