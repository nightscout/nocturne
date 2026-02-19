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
          container:
            "border-emerald-200 bg-emerald-50/50 dark:border-emerald-800/50 dark:bg-emerald-950/20",
          icon: "text-emerald-600 dark:text-emerald-400",
          badge:
            "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/50 dark:text-emerald-300",
        },
        warning: {
          container:
            "border-amber-200 bg-amber-50/50 dark:border-amber-800/50 dark:bg-amber-950/20",
          icon: "text-amber-600 dark:text-amber-400",
          badge:
            "bg-amber-100 text-amber-700 dark:bg-amber-900/50 dark:text-amber-300",
        },
        info: {
          container:
            "border-sky-200 bg-sky-50/50 dark:border-sky-800/50 dark:bg-sky-950/20",
          icon: "text-sky-600 dark:text-sky-400",
          badge: "bg-sky-100 text-sky-700 dark:bg-sky-900/50 dark:text-sky-300",
        },
        action: {
          container:
            "border-violet-200 bg-violet-50/50 dark:border-violet-800/50 dark:bg-violet-950/20",
          icon: "text-violet-600 dark:text-violet-400",
          badge:
            "bg-violet-100 text-violet-700 dark:bg-violet-900/50 dark:text-violet-300",
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
  import {
    formatInsight,
    getInsightTypeFromKey,
    type FormattedInsight,
  } from "$lib/utils/insight-localization";

  interface Insight extends FormattedInsight {
    clinicalNote?: string;
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

  // Use backend insights - backend is always the source of truth
  const insights = $derived.by(() => {
    const result: Insight[] = [];

    if (!analysis?.clinicalAssessment) return result;

    const assessment = analysis.clinicalAssessment;
    let priority = 1;

    // Add strengths
    if (assessment.strengths?.length) {
      assessment.strengths.forEach((strength) => {
        const formattedInsight = formatInsight(strength, "success", "pattern", priority);
        result.push(formattedInsight);
        priority++;
      });
    }

    // Add priority areas
    if (assessment.priorityAreas?.length) {
      assessment.priorityAreas.forEach((area) => {
        const type = getInsightTypeFromKey(area.key ?? "", "priority");
        const formattedInsight = formatInsight(area, type, "pattern", priority);
        result.push(formattedInsight);
        priority++;
      });
    }

    // Add actionable insights
    if (assessment.actionableInsights?.length) {
      assessment.actionableInsights.forEach((action) => {
        const type = getInsightTypeFromKey(action.key ?? "", "actionable");
        const formattedInsight = formatInsight(action, type, "pattern", priority);
        result.push(formattedInsight);
        priority++;
      });
    }

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
                <h4 class="text-sm font-semibold text-foreground">
                  {insight.title}
                </h4>
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
                    For healthcare providers
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
