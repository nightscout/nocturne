<script lang="ts">
  import { washInterior } from '$lib/watercolour-wash';
  import type { TreatmentFood } from "$lib/api";
  import { cn } from "$lib/utils";
  import { BarChart } from "layerchart";
  import { untrack } from "svelte";
  import { Artwork, seedFromName } from "@nocturne/watercolour";

  interface Props {
    /** Total carbs in the treatment */
    totalCarbs: number;
    /** Foods attributed to this treatment */
    foods: TreatmentFood[];
    /** Stable id of the meal or treatment; seeds its wash so equal-carb meals differ */
    seedKey?: string;
    /** Additional CSS classes */
    class?: string;
  }

  let { totalCarbs, foods, seedKey, class: className }: Props = $props();

  function cssGeometry(element: HTMLElement, properties: Record<string, string>) {
    const update = (next: Record<string, string>) => {
      for (const [name, value] of Object.entries(next)) element.style.setProperty(name, value);
    };
    update(properties);
    return { update };
  }

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

  const attributedCarbs = $derived(
    foods.reduce((sum, f) => sum + (f.carbs ?? 0), 0)
  );

  const unattributedCarbs = $derived(Math.max(0, totalCarbs - attributedCarbs));

  const chartKey = $derived(
    foods.map((f) => f.id ?? "").join("-") + `-${unattributedCarbs > 0}`
  );

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
        color: colorPalette[index % colorPalette.length],
        label: food.foodName ?? food.note ?? "Other",
      });
    });

    if (unattributedCarbs > 0) {
      config.push({
        key: "unattributed",
        color: unattributedColor,
        label: "Unattributed",
      });
    }

    return config;
  });

  const chartData = $derived.by(() => {
    if (totalCarbs <= 0) return [];

    const data: Record<string, number | string> = { category: "carbs" };

    foods.forEach((food, index) => {
      const key = food.id ?? `food-${index}`;
      data[key] = food.carbs ?? 0;
    });

    if (unattributedCarbs > 0) {
      data["unattributed"] = unattributedCarbs;
    }

    return [data];
  });

  const shouldShowChart = $derived(totalCarbs > 0);

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
      const previous = untrack(() => paintBox);
      if (next?.left !== previous?.left || next?.top !== previous?.top ||
          next?.width !== previous?.width || next?.height !== previous?.height) paintBox = next;
    };
    let frame: number | undefined;
    const scheduleMeasure = () => {
      if (frame !== undefined) return;
      frame = requestAnimationFrame(() => {
        frame = undefined;
        measure();
      });
    };
    scheduleMeasure();
    const mutations = new MutationObserver(scheduleMeasure);
    mutations.observe(el, {
      subtree: true,
      childList: true,
      attributes: true,
      attributeFilter: ["x", "y", "width", "height", "fill"],
    });
    const resizes = new ResizeObserver(scheduleMeasure);
    resizes.observe(el);
    return () => {
      if (frame !== undefined) cancelAnimationFrame(frame);
      mutations.disconnect();
      resizes.disconnect();
    };
  });
</script>

<div class={cn("h-8 flex justify-end", className)}>
  {#if shouldShowChart && seriesConfig.length > 0}
    <div class="relative isolate h-full w-(--chart-w)" use:cssGeometry={{ '--chart-w': `${chartWidthPercent}%` }}>
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
        <!-- Soft-light is too faint over these small, light bars. -->
        <div
          aria-hidden="true"
          data-carb-wash
          class="pointer-events-none absolute top-(--paint-y) left-(--paint-x) h-(--paint-h) w-(--paint-w) overflow-hidden"
          use:cssGeometry={{
            '--paint-x': `${paintBox.left}px`,
            '--paint-y': `${paintBox.top}px`,
            '--paint-w': `${paintBox.width}px`,
            '--paint-h': `${paintBox.height}px`,
          }}
        >
          <div class="absolute inset-0 wash-grain mix-blend-multiply">
            <Artwork
              artwork="wash"
              palette="slate"
              seed={seedKey ? seedFromName(seedKey) : totalCarbs}
              surface="light"
              autoplay="never"
              releaseAfterFinish
              fit="fill"
              crop={washInterior}
              class="size-full"
            />
          </div>
        </div>
      {/if}
    </div>
  {/if}
</div>

<style>
  /* Grey first, for the reason given at GlucoseTileWash's .wash-grain. */
  .wash-grain {
    filter: grayscale(1) brightness(2.6) contrast(1.15);
  }
</style>
