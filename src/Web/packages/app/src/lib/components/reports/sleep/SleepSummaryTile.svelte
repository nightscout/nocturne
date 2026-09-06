<script lang="ts" module>
  export interface TileDelta {
    text: string;
    direction: "up" | "down" | "flat";
    /** "good"/"bad" colors the arrow; "neutral" always renders muted regardless of direction. */
    tone: "good" | "bad" | "neutral";
    title?: string;
  }
</script>

<script lang="ts">
  /**
   * A single summary tile for the sleep trends page. Shared so the tile
   * markup/delta-arrow logic (adapted from the comparison report's diff rows)
   * isn't repeated per metric.
   */
  import { Card, CardContent, CardHeader, CardTitle } from "$lib/components/ui/card";
  import { TrendingUp, TrendingDown, Minus } from "lucide-svelte";

  // lucide-svelte's ambient types are legacy Svelte-4-style class components,
  // which don't structurally match Svelte 5's `Component<Props>` — same `any`
  // escape hatch used elsewhere for icon props (e.g. report-navigation.ts).
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  type IconComponent = any;

  interface Props {
    icon: IconComponent;
    iconClass?: string;
    label: string;
    value: string;
    unit?: string;
    caption?: string;
    delta?: TileDelta | null;
  }

  let {
    icon: Icon,
    iconClass = "text-muted-foreground",
    label,
    value,
    unit,
    caption,
    delta = null,
  }: Props = $props();

  const DeltaIcon = $derived(
    delta?.direction === "up" ? TrendingUp : delta?.direction === "down" ? TrendingDown : Minus
  );

  const deltaColor = $derived(
    !delta || delta.tone === "neutral"
      ? "var(--muted-foreground)"
      : delta.tone === "good"
        ? "var(--glucose-in-range)"
        : "var(--glucose-very-low)"
  );
</script>

<Card>
  <CardHeader class="pb-2">
    <CardTitle class="text-sm font-medium text-muted-foreground">{label}</CardTitle>
  </CardHeader>
  <CardContent>
    <div class="flex items-center gap-2">
      <Icon class="h-5 w-5 {iconClass}" />
      <span class="text-2xl font-bold tabular-nums">{value}</span>
      {#if unit}
        <span class="text-sm text-muted-foreground">{unit}</span>
      {/if}
      {#if delta}
        <span
          class="ml-auto flex shrink-0 items-center gap-0.5 text-xs font-semibold tabular-nums"
          style="color: {deltaColor};"
          title={delta.title ?? "vs prior 7 nights"}
        >
          <DeltaIcon class="h-3.5 w-3.5" />
          {delta.text}
        </span>
      {/if}
    </div>
    {#if caption}
      <p class="mt-1 text-xs text-muted-foreground">{caption}</p>
    {/if}
  </CardContent>
</Card>
