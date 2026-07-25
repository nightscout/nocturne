<script lang="ts" module>
  import { tv, type VariantProps } from "tailwind-variants";

  export const scoreCardVariants = tv({
    slots: {
      card: "relative overflow-hidden border-2 transition-all hover:shadow-md",
      strip: "absolute top-0 left-0 right-0 h-1",
      value: "text-4xl font-bold",
    },
    variants: {
      status: {
        excellent: {
          card: "border-green-200 dark:border-green-800",
          strip: "bg-green-500",
          value: "text-green-700 dark:text-green-300",
        },
        good: {
          card: "border-blue-200 dark:border-blue-800",
          strip: "bg-blue-500",
          value: "text-blue-700 dark:text-blue-300",
        },
        fair: {
          card: "border-yellow-200 dark:border-yellow-800",
          strip: "bg-yellow-500",
          value: "text-yellow-700 dark:text-yellow-300",
        },
        "needs-attention": {
          card: "border-orange-200 dark:border-orange-800",
          strip: "bg-orange-500",
          value: "text-orange-700 dark:text-orange-300",
        },
        critical: {
          card: "border-red-200 dark:border-red-800",
          strip: "bg-red-500",
          value: "text-red-700 dark:text-red-300",
        },
      },
    },
    defaultVariants: {
      status: "good",
    },
  });

  export type ScoreCardStatus = VariantProps<
    typeof scoreCardVariants
  >["status"];
</script>

<script lang="ts">
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Progress } from "$lib/components/ui/progress";
  import { TrendingUp, TrendingDown, Minus, Info } from "lucide-svelte";
  import type { Snippet } from "svelte";
  import { cn } from "$lib/utils";

  interface Props {
    title: string;
    value: number | string;
    unit?: string;
    explanation: string;
    clinicalContext?: string;
    targetRange?: { min?: number; max?: number; optimal?: number };
    trend?: "up" | "down" | "stable";
    trendLabel?: string;
    status?: ScoreCardStatus;
    icon?: Snippet;
    showProgress?: boolean;
    progressValue?: number;
    progressMax?: number;
    colorClass?: string;
  }

  let {
    title,
    value,
    unit,
    explanation,
    clinicalContext,
    targetRange,
    trend,
    trendLabel,
    status = "good",
    icon,
    showProgress = false,
    progressValue = 0,
    progressMax = 100,
    colorClass,
  }: Props = $props();

  const styles = $derived(scoreCardVariants({ status }));

  const TrendIcon = $derived(
    trend === "up" ? TrendingUp : trend === "down" ? TrendingDown : Minus
  );
</script>

<Card class={cn("relative overflow-hidden", styles.card())}>
  <!-- Status indicator strip -->
  <div class={styles.strip()}></div>

  <CardHeader class="pb-2">
    <div class="flex items-center gap-2">
      {#if icon}
        {@render icon()}
      {/if}
      <CardTitle class="text-base font-medium">{title}</CardTitle>
    </div>
  </CardHeader>

  <CardContent class="space-y-3">
    <!-- Main Value Display -->
    <div class="flex items-baseline gap-2">
      <span class={cn("text-4xl font-bold", colorClass || styles.value())}>
        {value}
      </span>
      {#if unit}
        <span class="text-lg text-muted-foreground">{unit}</span>
      {/if}

      {#if trend}
        <div
          class={cn(
            "flex items-center gap-1 text-sm",
            trend === "up" && "text-orange-500",
            trend === "down" && "text-green-500",
            trend === "stable" && "text-muted-foreground"
          )}
        >
          <TrendIcon class="h-4 w-4" />
          {#if trendLabel}
            <span>{trendLabel}</span>
          {/if}
        </div>
      {/if}
    </div>

    <!-- Progress Bar (optional) -->
    {#if showProgress}
      <Progress value={progressValue} max={progressMax} class="h-2" />
    {/if}

    <!-- Target Range Indicator -->
    {#if targetRange}
      <div class="flex items-center gap-2 text-xs text-muted-foreground">
        <span>Target:</span>
        {#if targetRange.min !== undefined && targetRange.max !== undefined}
          <span class="font-medium">
            {targetRange.min} – {targetRange.max}{unit ? ` ${unit}` : ""}
          </span>
        {:else if targetRange.optimal !== undefined}
          <span class="font-medium">
            ≥ {targetRange.optimal}{unit ? ` ${unit}` : ""}
          </span>
        {/if}
      </div>
    {/if}

    <!-- Plain Language Explanation -->
    <p class="text-sm leading-relaxed text-muted-foreground">
      {explanation}
    </p>

    <!-- Clinical Context (expandable) -->
    {#if clinicalContext}
      <details class="group">
        <summary
          class="flex cursor-pointer items-center gap-1 text-xs text-blue-600 hover:underline dark:text-blue-400"
        >
          <Info class="h-3 w-3" />
          Clinical context
        </summary>
        <p class="mt-2 rounded bg-muted/50 p-2 text-xs text-muted-foreground">
          {clinicalContext}
        </p>
      </details>
    {/if}
  </CardContent>
</Card>
