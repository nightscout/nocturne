<script lang="ts" module>
  import { tv, type VariantProps } from "tailwind-variants";

  export const insightVariants = tv({
    slots: {
      container: "rounded-lg border p-4 transition-all",
      icon: "h-5 w-5",
      badge: "text-[10px]",
    },
    variants: {
      type: {
        success: {
          container: "border-emerald-200 bg-emerald-50/50 dark:border-emerald-800/50 dark:bg-emerald-950/20",
          icon: "text-emerald-600 dark:text-emerald-400",
          badge: "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/50 dark:text-emerald-300",
        },
        warning: {
          container: "border-amber-200 bg-amber-50/50 dark:border-amber-800/50 dark:bg-amber-950/20",
          icon: "text-amber-600 dark:text-amber-400",
          badge: "bg-amber-100 text-amber-700 dark:bg-amber-900/50 dark:text-amber-300",
        },
        info: {
          container: "border-sky-200 bg-sky-50/50 dark:border-sky-800/50 dark:bg-sky-950/20",
          icon: "text-sky-600 dark:text-sky-400",
          badge: "bg-sky-100 text-sky-700 dark:bg-sky-900/50 dark:text-sky-300",
        },
        action: {
          container: "border-violet-200 bg-violet-50/50 dark:border-violet-800/50 dark:bg-violet-950/20",
          icon: "text-violet-600 dark:text-violet-400",
          badge: "bg-violet-100 text-violet-700 dark:bg-violet-900/50 dark:text-violet-300",
        },
      },
    },
    defaultVariants: {
      type: "info",
    },
  });

  export type InsightType = VariantProps<typeof insightVariants>["type"];
</script>

<script lang="ts">
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import {
    Lightbulb,
    TrendingUp,
    AlertCircle,
    CheckCircle2,
    Clock,
    Dumbbell,
    Pill,
  } from "lucide-svelte";
  import type { GlucoseAnalytics } from "$lib/api";

  interface Insight {
    type: NonNullable<InsightType>;
    category: "pattern" | "treatment" | "lifestyle" | "trend";
    title: string;
    description: string;
    clinicalNote?: string;
    priority: number;
  }

  interface Props {
    analysis: GlucoseAnalytics;
    showClinicalNotes?: boolean;
    maxInsights?: number;
  }

  let { analysis, showClinicalNotes = true, maxInsights = 5 }: Props = $props();

  const typeIcons: Record<NonNullable<InsightType>, typeof CheckCircle2> = {
    success: CheckCircle2,
    warning: AlertCircle,
    info: Lightbulb,
    action: TrendingUp,
  };

  const categoryIcons = {
    pattern: Clock,
    treatment: Pill,
    lifestyle: Dumbbell,
    trend: TrendingUp,
  };

  // Generate insights based on analysis data
  const insights = $derived.by(() => {
    const result: Insight[] = [];
    const tir = analysis?.timeInRange?.percentages;
    const variability = analysis?.glycemicVariability;

    if (!tir || !variability) return result;

    const tirTarget = tir.target ?? 0;

    // Time in Range insights
    if (tirTarget >= 70) {
      result.push({
        type: "success",
        category: "pattern",
        title: "Excellent Time in Range",
        description: `You're spending ${tirTarget.toFixed(0)}% of your time in range — that's above the recommended 70% target!`,
        clinicalNote:
          "TIR >70% is associated with reduced risk of diabetes complications (ADA Standards of Care 2024).",
        priority: 1,
      });
    } else if (tirTarget >= 50) {
      result.push({
        type: "info",
        category: "pattern",
        title: "Good Progress on Time in Range",
        description: `Your TIR is ${tirTarget.toFixed(0)}%. The goal is 70% or higher — you're making progress!`,
        clinicalNote:
          "Each 5% improvement in TIR is clinically meaningful for reducing complications risk.",
        priority: 2,
      });
    } else {
      result.push({
        type: "action",
        category: "pattern",
        title: "Time in Range Needs Attention",
        description: `Your TIR is ${tirTarget.toFixed(0)}%. Let's work together to find patterns and improve this number.`,
        clinicalNote:
          "Consider reviewing insulin:carb ratios, correction factors, and meal timing with your care team.",
        priority: 1,
      });
    }

    // Low blood sugar insights
    const totalLows = (tir.low ?? 0) + (tir.severeLow ?? 0);
    if (totalLows > 4) {
      result.push({
        type: "warning",
        category: "pattern",
        title: "Frequent Low Blood Sugars",
        description: `You're spending ${totalLows.toFixed(1)}% of time below range. The goal is less than 4%. Let's identify when these lows occur.`,
        clinicalNote:
          "Time below range <54 mg/dL should be <1%. Review hypoglycemia patterns, especially overnight and post-exercise.",
        priority: 1,
      });
    } else if (totalLows < 1) {
      result.push({
        type: "success",
        category: "pattern",
        title: "Minimal Low Blood Sugars",
        description:
          "Great job avoiding lows! You're spending very little time below range.",
        priority: 3,
      });
    }

    // Variability insights
    if (variability.coefficientOfVariation) {
      const cv = variability.coefficientOfVariation;
      if (cv <= 33) {
        result.push({
          type: "success",
          category: "pattern",
          title: "Stable Glucose Levels",
          description: `Your glucose variability (CV) is ${cv.toFixed(0)}% — that's nicely stable! Lower variability means fewer unexpected swings.`,
          clinicalNote:
            "CV ≤33% indicates stable glycemic control and is associated with reduced hypoglycemia risk.",
          priority: 2,
        });
      } else if (cv <= 40) {
        result.push({
          type: "info",
          category: "pattern",
          title: "Moderate Glucose Variability",
          description: `Your CV is ${cv.toFixed(0)}%. Some swings are normal, but there may be room to smooth things out.`,
          clinicalNote:
            "CV between 33-36% is acceptable. Consider evaluating meal composition and timing.",
          priority: 3,
        });
      } else {
        result.push({
          type: "action",
          category: "pattern",
          title: "High Glucose Variability",
          description: `Your glucose is swinging quite a bit (CV: ${cv.toFixed(0)}%). This can feel exhausting — let's find the causes.`,
          clinicalNote:
            "High CV (>36%) increases hypoglycemia risk and may indicate need for treatment optimization.",
          priority: 2,
        });
      }
    }

    // HbA1c insights
    if (variability.estimatedA1c) {
      const a1c = variability.estimatedA1c;
      if (a1c < 7.0) {
        result.push({
          type: "success",
          category: "trend",
          title: "Excellent Estimated A1C",
          description: `Your estimated A1C of ${a1c.toFixed(1)}% is below the typical target of 7%. Great management!`,
          clinicalNote:
            "A1C <7% reduces microvascular complication risk. Ensure this isn't at the expense of increased hypoglycemia.",
          priority: 2,
        });
      } else if (a1c <= 7.5) {
        result.push({
          type: "info",
          category: "trend",
          title: "Good A1C Estimate",
          description: `Your estimated A1C is ${a1c.toFixed(1)}%. You're near the common target range.`,
          priority: 3,
        });
      }
    }

    // High blood sugar insights
    const totalHighs = (tir.high ?? 0) + (tir.severeHigh ?? 0);
    if (totalHighs > 25) {
      result.push({
        type: "action",
        category: "pattern",
        title: "Time Above Range",
        description: `You're spending ${totalHighs.toFixed(0)}% of time above 180 mg/dL. Let's look at when highs occur most often.`,
        clinicalNote:
          "Target is <25% time above range. Review post-meal patterns, correction dosing, and basal rates.",
        priority: 2,
      });
    }

    // Sort by priority and limit
    return result.sort((a, b) => a.priority - b.priority).slice(0, maxInsights);
  });
</script>

<Card class="border">
  <CardHeader>
    <CardTitle class="flex items-center gap-2">
      <Lightbulb class="h-5 w-5 text-amber-500" />
      What Your Data is Telling Us
    </CardTitle>
  </CardHeader>
  <CardContent class="space-y-3">
    {#if insights.length === 0}
      <p class="py-4 text-center text-sm text-muted-foreground">
        Not enough data yet to generate insights. Keep tracking!
      </p>
    {:else}
      {#each insights as insight}
        {@const styles = insightVariants({ type: insight.type })}
        {@const InsightIcon = typeIcons[insight.type]}
        {@const CategoryIcon = categoryIcons[insight.category]}

        <div class={styles.container()}>
          <div class="flex items-start gap-3">
            <div class="mt-0.5 shrink-0">
              <InsightIcon class={styles.icon()} />
            </div>
            <div class="min-w-0 flex-1 space-y-1.5">
              <div class="flex flex-wrap items-center gap-2">
                <h4 class="text-sm font-semibold text-foreground">{insight.title}</h4>
                <Badge variant="outline" class={styles.badge()}>
                  <CategoryIcon class="mr-1 h-3 w-3" />
                  {insight.category}
                </Badge>
              </div>
              <p class="text-sm leading-relaxed text-foreground/80">
                {insight.description}
              </p>
              {#if showClinicalNotes && insight.clinicalNote}
                <details class="group pt-1">
                  <summary
                    class="cursor-pointer text-xs text-muted-foreground hover:text-foreground"
                  >
                    ▸ For healthcare providers
                  </summary>
                  <p
                    class="mt-2 rounded-md border bg-background/50 p-2.5 text-xs leading-relaxed text-muted-foreground"
                  >
                    {insight.clinicalNote}
                  </p>
                </details>
              {/if}
            </div>
          </div>
        </div>
      {/each}
    {/if}
  </CardContent>
</Card>
