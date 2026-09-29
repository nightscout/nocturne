<script lang="ts">
  import type { TreatmentFood } from "$lib/api";
  import { cn } from "$lib/utils";
  import { BarChart } from "layerchart";
  import { untrack } from "svelte";
  import { Artwork } from "@nocturne/watercolour";

  interface Props {
    /** Total carbs in the treatment */
    totalCarbs: number;
    /** Foods attributed to this treatment */
    foods: TreatmentFood[];
    /** Additional CSS classes */
    class?: string;
  }

  let { totalCarbs, foods, class: className }: Props = $props();

  // Color palette for food segments
  const colorPalette = [
    "oklch(0.765 0.177 163.223)", // emerald-500
    "oklch(0.623 0.214 259.815)", // blue-500
    "oklch(0.769 0.188 70.08)", // amber-500
    "oklch(0.645 0.246 16.439)", // rose-500
    "oklch(0.627 0.265 303.9)", // purple-500
    "oklch(0.715 0.143 215.221)", // cyan-500
    "oklch(0.702 0.183 55.934)", // orange-500
    "oklch(0.768 0.233 130.85)", // lime-500
  ];

  const unattributedColor = "oklch(0.556 0.046 257.417)"; // muted gray

  function getColorForIndex(index: number): string {
    return colorPalette[index % colorPalette.length];
  }

  // Calculate attributed carbs first
  const attributedCarbs = $derived(
    foods.reduce((sum, f) => sum + (f.carbs ?? 0), 0)
  );

  // Calculate unattributed carbs
  const unattributedCarbs = $derived(Math.max(0, totalCarbs - attributedCarbs));

  // Generate a stable key for the chart based on food IDs to force re-render
  const chartKey = $derived(
    foods.map((f) => f.id ?? "").join("-") + `-${unattributedCarbs > 0}`
  );

  // Build series config for each food + unattributed
  const seriesConfig = $derived.by(() => {
    if (totalCarbs <= 0) return [];

    const config: Array<{
      key: string;
      color: string;
      label: string;
    }> = [];

    foods.forEach((food, index) => {
      const key = food.id ?? `food-${index}`;
      config.push({
        key,
        color: getColorForIndex(index),
        label: food.foodName ?? food.note ?? "Other",
      });
    });

    // Add unattributed segment
    if (unattributedCarbs > 0) {
      config.push({
        key: "unattributed",
        color: unattributedColor,
        label: "Unattributed",
      });
    }

    return config;
  });

  // Build data object with carb values for each food
  const chartData = $derived.by(() => {
    if (totalCarbs <= 0) return [];

    const data: Record<string, number | string> = { category: "carbs" };

    foods.forEach((food, index) => {
      const key = food.id ?? `food-${index}`;
      data[key] = food.carbs ?? 0;
    });

    // Add unattributed
    if (unattributedCarbs > 0) {
      data["unattributed"] = unattributedCarbs;
    }

    return [data];
  });

  // Always show chart if there are carbs
  const shouldShowChart = $derived(totalCarbs > 0);

  // Calculate width as percentage of max carbs (100g = 100%)
  // with minimum of 20% so small amounts are still visible
  const MAX_CARBS = 100;
  const MIN_WIDTH_PERCENT = 20;
  const chartWidthPercent = $derived(
    Math.max(MIN_WIDTH_PERCENT, Math.min(100, (totalCarbs / MAX_CARBS) * 100))
  );

  // The attributed bars are read back off the rendered chart: layerchart nices
  // the x domain, so their extent cannot be derived from the carbs alone.
  let chartEl = $state<HTMLDivElement>();
  let paintBox = $state<{ left: number; top: number; width: number; height: number }>();

  $effect(() => {
    const el = chartEl;
    if (!el) return;
    const measure = () => {
      const host = el.getBoundingClientRect();
      let left = Infinity, top = Infinity, right = -Infinity, bottom = -Infinity;
      for (const rect of el.querySelectorAll("rect")) {
        if (!colorPalette.includes(rect.getAttribute("fill") ?? "")) continue;
        const box = rect.getBoundingClientRect();
        if (box.width <= 0 || box.height <= 0) continue;
        left = Math.min(left, box.left);
        top = Math.min(top, box.top);
        right = Math.max(right, box.right);
        bottom = Math.max(bottom, box.bottom);
      }
      const next = right > left
        ? { left: left - host.left, top: top - host.top, width: right - left, height: bottom - top }
        : undefined;
      if (JSON.stringify(next) !== JSON.stringify(untrack(() => paintBox))) paintBox = next;
    };
    measure();
    const mutations = new MutationObserver(measure);
    mutations.observe(el, { subtree: true, childList: true, attributes: true });
    const resizes = new ResizeObserver(measure);
    resizes.observe(el);
    return () => {
      mutations.disconnect();
      resizes.disconnect();
    };
  });
</script>

<div class={cn("h-8 flex justify-end", className)}>
  {#if shouldShowChart && seriesConfig.length > 0}
    <div class="relative isolate h-full w-(--chart-w)" style:--chart-w="{chartWidthPercent}%">
      {#key chartKey}
        <div bind:this={chartEl} class="h-full">
        <BarChart
          data={chartData}
          orientation="horizontal"
          y="category"
          series={seriesConfig}
          seriesLayout="stack"
          axis={false}
          grid={false}
          highlight
          rule={false}
          padding={{ left: 0, right: 0, top: 0, bottom: 0 }}
          props={{
            bars: {
              strokeWidth: 0,
              radius: 0,
            },

            tooltip: {
              context: { mode: "bounds" },
              header: { format: "none" },
              item: {
                format: "decimal",
              },
            },
          }}
        />
        </div>
      {/key}
      {#if paintBox}
        <!-- A grey wash multiplied over the attributed bars: the paint takes each bar's own hue, and
             the crop keeps only the wash's interior so no dried edge floats inside the bar. -->
        <div
          aria-hidden="true"
          data-carb-wash
          class="pointer-events-none absolute overflow-hidden"
          style:left="{paintBox.left}px"
          style:top="{paintBox.top}px"
          style:width="{paintBox.width}px"
          style:height="{paintBox.height}px"
        >
          <div class="absolute -top-full -left-[46%] h-[303%] w-[192%] mix-blend-multiply [filter:grayscale(1)_brightness(2.7)_contrast(1.25)]">
            <Artwork
              artwork="wash"
              palette="slate"
              surface="light"
              autoplay="never"
              releaseAfterFinish
              fit="fill"
              class="size-full"
            />
          </div>
        </div>
      {/if}
    </div>
  {/if}
</div>
