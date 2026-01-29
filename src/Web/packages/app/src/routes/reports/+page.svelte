<script lang="ts" module>
  import { tv, type VariantProps } from "tailwind-variants";

  export const categoryVariants = tv({
    slots: {
      card: "group relative overflow-hidden rounded-2xl border-0 bg-linear-to-br transition-all duration-500 hover:scale-[1.02] hover:shadow-2xl",
      iconWrap:
        "flex h-14 w-14 items-center justify-center rounded-xl transition-transform duration-500 group-hover:scale-110",
      icon: "h-7 w-7",
      title: "text-xl font-semibold tracking-tight",
      subtitle: "text-sm font-medium opacity-80",
    },
    variants: {
      category: {
        overview: {
          card: "from-blue-50 to-indigo-100/80 dark:from-blue-950/50 dark:to-indigo-900/30",
          iconWrap: "bg-blue-500/20 dark:bg-blue-400/20",
          icon: "text-blue-600 dark:text-blue-300",
          title: "text-blue-900 dark:text-blue-100",
          subtitle: "text-blue-700/80 dark:text-blue-300/80",
        },
        patterns: {
          card: "from-violet-50 to-purple-100/80 dark:from-violet-950/50 dark:to-purple-900/30",
          iconWrap: "bg-violet-500/20 dark:bg-violet-400/20",
          icon: "text-violet-600 dark:text-violet-300",
          title: "text-violet-900 dark:text-violet-100",
          subtitle: "text-violet-700/80 dark:text-violet-300/80",
        },
        lifestyle: {
          card: "from-emerald-50 to-teal-100/80 dark:from-emerald-950/50 dark:to-teal-900/30",
          iconWrap: "bg-emerald-500/20 dark:bg-emerald-400/20",
          icon: "text-emerald-600 dark:text-emerald-300",
          title: "text-emerald-900 dark:text-emerald-100",
          subtitle: "text-emerald-700/80 dark:text-emerald-300/80",
        },
        treatment: {
          card: "from-amber-50 to-orange-100/80 dark:from-amber-950/50 dark:to-orange-900/30",
          iconWrap: "bg-amber-500/20 dark:bg-amber-400/20",
          icon: "text-amber-600 dark:text-amber-300",
          title: "text-amber-900 dark:text-amber-100",
          subtitle: "text-amber-700/80 dark:text-amber-300/80",
        },
      },
    },
    defaultVariants: {
      category: "overview",
    },
  });

  export type CategoryType = VariantProps<typeof categoryVariants>["category"];
</script>

<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import { cn } from "$lib/utils";
  import {
    TrendingUp,
    BarChart3,
    Clock,
    Calendar,
    FileText,
    Gauge,
    AlertTriangle,
    Moon,
    Utensils,
    Dumbbell,
    Heart,
    Stethoscope,
    ArrowRight,
    Layers,
    PieChart,
    Sparkles,
    Activity,
    ChevronRight,
  } from "lucide-svelte";
  import TIRStackedChart from "$lib/components/reports/TIRStackedChart.svelte";
  import { AmbulatoryGlucoseProfile } from "$lib/components/ambulatory-glucose-profile";
  import type { ScoreCardStatus } from "$lib/components/reports/GlucoseScoreCard.svelte";
  import { getReportsData } from "$lib/data/reports.remote";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import {
    formatGlucoseValue,
    formatGlucoseRange,
    getUnitLabel,
  } from "$lib/utils/formatting";
  import ReportsSkeleton from "$lib/components/reports/ReportsSkeleton.svelte";
  import SiteChangeIcon from "$lib/components/icons/SiteChangeIcon.svelte";
  import { resource } from "runed";
  import { fly, fade, scale } from "svelte/transition";
  import { cubicOut, elasticOut } from "svelte/easing";

  // Get shared date params from context (set by reports layout)
  // Default: 14 days is standard for reports overview
  const reportsParams = requireDateParamsContext(14);

  // Use resource for controlled reactivity
  const reportsResource = resource(
    () => reportsParams.dateRangeInput,
    async (dateRangeInput) => {
      return await getReportsData(dateRangeInput);
    },
    { debounce: 100 }
  );

  const isLoading = $derived(reportsResource.loading);
  const queryData = $derived(reportsResource.current);
  const entries = $derived(queryData?.entries ?? []);
  const analysis = $derived(queryData?.analysis);
  const averagedStats = $derived(queryData?.averagedStats);
  const dateRange = $derived(
    queryData?.dateRange ?? {
      from: new Date().toISOString(),
      to: new Date().toISOString(),
      lastUpdated: new Date().toISOString(),
    }
  );

  const units = $derived(glucoseUnits.current);
  const glucoseFormatting = $derived({
    unitLabel: getUnitLabel(units),
    targetRangeDisplay: formatGlucoseRange(70, 180, units),
  });

  const tir = $derived(analysis?.timeInRange?.percentages);
  const variability = $derived(analysis?.glycemicVariability);
  const stats = $derived(analysis?.basicStats);

  // Status helpers
  function getTIRStatus(tirValue: number): ScoreCardStatus {
    if (tirValue >= 70) return "excellent";
    if (tirValue >= 60) return "good";
    if (tirValue >= 50) return "fair";
    if (tirValue >= 40) return "needs-attention";
    return "critical";
  }

  function getStatusColor(status: ScoreCardStatus): string {
    // Uses nocturne theme status colors where semantically relevant
    const colors = {
      excellent: "from-glucose-in-range to-glucose-in-range",
      good: "from-glucose-in-range to-glucose-in-range",
      fair: "from-status-warning to-status-warning",
      "needs-attention": "from-status-warning to-status-critical",
      critical: "from-status-critical to-status-critical",
    };
    return colors[status ?? "good"];
  }

  function getStatusLabel(status: ScoreCardStatus): string {
    const labels = {
      excellent: "Excellent",
      good: "Good",
      fair: "Fair",
      "needs-attention": "Needs Attention",
      critical: "Critical",
    };
    return labels[status ?? "good"];
  }

  // Report categories
  const reportCategories = [
    {
      id: "overview",
      title: "The Big Picture",
      subtitle: "Your key metrics at a glance",
      icon: Gauge,
      reports: [
        {
          title: "Executive Summary",
          description: "All your important numbers in one place",
          href: "/reports/executive-summary",
          icon: Gauge,
          status: "available" as const,
        },
        {
          title: "Glucose Profile (AGP)",
          description: "Your typical day's glucose pattern",
          href: "/reports/agp",
          icon: BarChart3,
          status: "available" as const,
        },
        {
          title: "Glucose Distribution",
          description: "Time spent in each glucose zone",
          href: "/reports/glucose-distribution",
          icon: PieChart,
          status: "available" as const,
        },
      ],
    },
    {
      id: "patterns",
      title: "Patterns & Trends",
      subtitle: "Discover what affects your glucose",
      icon: TrendingUp,
      reports: [
        {
          title: "Day-by-Day View",
          description: "Review each day individually",
          href: "/reports/readings",
          icon: Calendar,
          status: "available" as const,
        },
        {
          title: "Week to Week",
          description: "Compare patterns across days",
          href: "/reports/week-to-week",
          icon: Layers,
          status: "available" as const,
        },
        {
          title: "Hourly Patterns",
          description: "Find your best and worst hours",
          href: "/reports/hourly-stats",
          icon: Clock,
          status: "coming-soon" as const,
        },
      ],
    },
    {
      id: "lifestyle",
      title: "Lifestyle Impact",
      subtitle: "How food, exercise & sleep affect you",
      icon: Heart,
      reports: [
        {
          title: "Meal Analysis",
          description: "See how different meals affect you",
          href: "/reports/meals",
          icon: Utensils,
          status: "coming-soon" as const,
        },
        {
          title: "Exercise Impact",
          description: "Track activity's effect on glucose",
          href: "/reports/exercise",
          icon: Dumbbell,
          status: "coming-soon" as const,
        },
        {
          title: "Sleep & Overnight",
          description: "Understand your overnight patterns",
          href: "/reports/sleep",
          icon: Moon,
          status: "coming-soon" as const,
        },
      ],
    },
    {
      id: "treatment",
      title: "Treatment Insights",
      subtitle: "Is your treatment working?",
      icon: Stethoscope,
      reports: [
        {
          title: "Treatment Log",
          description: "Your insulin and carb history",
          href: "/reports/treatments",
          icon: FileText,
          status: "available" as const,
        },
        {
          title: "Basal Rate Analysis",
          description: "How your basal rates vary",
          href: "/reports/basal-analysis",
          icon: Layers,
          status: "available" as const,
        },
        {
          title: "Insulin Delivery",
          description: "Basal vs bolus breakdown",
          href: "/reports/insulin-delivery",
          icon: PieChart,
          status: "available" as const,
        },
        {
          title: "Site Change Impact",
          description: "How site changes affect control",
          href: "/reports/site-change-impact",
          icon: SiteChangeIcon,
          status: "available" as const,
        },
      ],
    },
  ];

  // Animation delay helper
  function staggerDelay(index: number): number {
    return 80 + index * 60;
  }
</script>

<svelte:head>
  <title>Reports - Nocturne</title>
  <meta
    name="description"
    content="Comprehensive diabetes management analytics and insights"
  />
</svelte:head>

{#if isLoading && !reportsResource.current}
  <ReportsSkeleton />
{:else if reportsResource.error}
  <div class="flex min-h-[60vh] items-center justify-center px-4">
    <div class="max-w-md space-y-4 text-center" in:fade={{ duration: 300 }}>
      <div
        class="mx-auto flex h-16 w-16 items-center justify-center rounded-full bg-red-100 dark:bg-red-900/30"
      >
        <AlertTriangle class="h-8 w-8 text-red-500" />
      </div>
      <h2 class="text-xl font-semibold">Unable to load reports</h2>
      <p class="text-muted-foreground">
        {reportsResource.error instanceof Error
          ? reportsResource.error.message
          : "Something went wrong"}
      </p>
      <Button variant="outline" onclick={() => reportsResource.refetch()}>
        Try again
      </Button>
    </div>
  </div>
{:else}
  <div class="min-h-screen">
    <!-- Hero Section with Key Metrics -->
    <section
      class="relative overflow-hidden bg-linear-to-b from-slate-50 via-white to-transparent pb-8 pt-6 dark:from-slate-900 dark:via-slate-950 dark:to-transparent"
    >
      <!-- Subtle decorative background -->
      <div
        class="pointer-events-none absolute inset-0 overflow-hidden opacity-30 dark:opacity-20"
      >
        <div
          class="absolute -right-20 -top-20 h-96 w-96 rounded-full bg-linear-to-br from-blue-200 to-purple-200 blur-3xl dark:from-blue-900 dark:to-purple-900"
        ></div>
        <div
          class="absolute -bottom-32 -left-20 h-80 w-80 rounded-full bg-linear-to-br from-emerald-200 to-teal-200 blur-3xl dark:from-emerald-900 dark:to-teal-900"
        ></div>
      </div>

      <div class="container relative mx-auto max-w-6xl px-4">
        <!-- Header -->
        <div
          class="mb-8 text-center"
          in:fly={{ y: -20, duration: 600, delay: 100, easing: cubicOut }}
        >
          <div
            class="mb-3 inline-flex items-center gap-2 rounded-full bg-primary/5 px-4 py-1.5 text-sm font-medium text-primary"
          >
            <Calendar class="h-4 w-4" />
            {new Date(dateRange.from).toLocaleDateString("en-US", {
              month: "short",
              day: "numeric",
            })} – {new Date(dateRange.to).toLocaleDateString("en-US", {
              month: "short",
              day: "numeric",
              year: "numeric",
            })}
          </div>
          <h1
            class="bg-linear-to-r from-slate-900 via-slate-700 to-slate-800 bg-clip-text text-4xl font-bold tracking-tight text-transparent dark:from-white dark:via-slate-200 dark:to-slate-300 md:text-5xl"
          >
            Your Glucose Report
          </h1>
          <p class="mt-3 text-lg text-muted-foreground">
            {entries.length.toLocaleString()} readings analyzed
          </p>
        </div>

        {#if analysis}
          {@const tirValue = tir?.target ?? 0}
          {@const tirStatus = getTIRStatus(tirValue)}
          <!-- Main Metric Hero Card -->
          <div
            class="mb-8"
            in:fly={{ y: 30, duration: 700, delay: 200, easing: cubicOut }}
          >
            <div
              class="relative overflow-hidden rounded-3xl bg-white p-8 shadow-xl shadow-slate-200/50 dark:bg-slate-900 dark:shadow-none dark:ring-1 dark:ring-white/10"
            >
              <!-- Gradient accent bar -->
              <div
                class={cn(
                  "absolute left-0 top-0 h-1.5 w-full bg-linear-to-r",
                  getStatusColor(tirStatus)
                )}
              ></div>

              <div class="grid items-center gap-8 lg:grid-cols-[1fr,auto,1fr]">
                <!-- Left: Time in Range highlight -->
                <div class="text-center lg:text-left">
                  <div class="mb-1 text-sm font-medium text-muted-foreground">
                    Time in Range
                  </div>
                  <div
                    class="flex items-baseline justify-center gap-2 lg:justify-start"
                  >
                    <span
                      class={cn(
                        "bg-linear-to-r bg-clip-text text-6xl font-bold tabular-nums text-transparent md:text-7xl",
                        getStatusColor(tirStatus)
                      )}
                    >
                      {tirValue.toFixed(0)}
                    </span>
                    <span class="text-2xl font-medium text-muted-foreground">
                      %
                    </span>
                  </div>
                  <Badge variant="secondary" class="mt-2 gap-1.5 px-3 py-1">
                    <Sparkles class="h-3 w-3" />
                    {getStatusLabel(tirStatus)}
                  </Badge>
                </div>

                <!-- Center: TIR Chart -->
                <div
                  class="flex justify-center h-96"
                  in:scale={{
                    start: 0.9,
                    duration: 600,
                    delay: 400,
                    easing: elasticOut,
                  }}
                >
                  <TIRStackedChart percentages={tir} />
                </div>

                <!-- Right: Secondary metrics -->
                <div class="grid grid-cols-2 gap-4 lg:gap-6">
                  <div
                    class="rounded-2xl bg-slate-50 p-4 text-center dark:bg-slate-800/50"
                  >
                    <div
                      class="text-xs font-medium uppercase tracking-wide text-muted-foreground"
                    >
                      Est. A1C
                    </div>
                    <div
                      class="mt-1 text-3xl font-bold tabular-nums text-slate-900 dark:text-slate-100"
                    >
                      {variability?.estimatedA1c?.toFixed(1) ?? "–"}
                      <span class="text-lg font-normal text-muted-foreground">
                        %
                      </span>
                    </div>
                  </div>
                  <div
                    class="rounded-2xl bg-slate-50 p-4 text-center dark:bg-slate-800/50"
                  >
                    <div
                      class="text-xs font-medium uppercase tracking-wide text-muted-foreground"
                    >
                      Variability
                    </div>
                    <div
                      class="mt-1 text-3xl font-bold tabular-nums text-slate-900 dark:text-slate-100"
                    >
                      {variability?.coefficientOfVariation?.toFixed(0) ?? "–"}
                      <span class="text-lg font-normal text-muted-foreground">
                        %
                      </span>
                    </div>
                  </div>
                  <div
                    class="rounded-2xl bg-slate-50 p-4 text-center dark:bg-slate-800/50"
                  >
                    <div
                      class="text-xs font-medium uppercase tracking-wide text-muted-foreground"
                    >
                      Average
                    </div>
                    <div
                      class="mt-1 text-3xl font-bold tabular-nums text-slate-900 dark:text-slate-100"
                    >
                      {stats?.mean
                        ? formatGlucoseValue(stats.mean, units)
                        : "–"}
                      <span class="text-sm font-normal text-muted-foreground">
                        {glucoseFormatting.unitLabel}
                      </span>
                    </div>
                  </div>
                  <div
                    class="rounded-2xl bg-slate-50 p-4 text-center dark:bg-slate-800/50"
                  >
                    <div
                      class="text-xs font-medium uppercase tracking-wide text-muted-foreground"
                    >
                      Time Low
                    </div>
                    <div
                      class="mt-1 text-3xl font-bold tabular-nums text-slate-900 dark:text-slate-100"
                    >
                      {((tir?.low ?? 0) + (tir?.severeLow ?? 0)).toFixed(1)}
                      <span class="text-lg font-normal text-muted-foreground">
                        %
                      </span>
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </div>

          <!-- AGP Preview -->
          <div
            class="rounded-2xl bg-white p-6 shadow-lg shadow-slate-200/30 dark:bg-slate-900/80 dark:shadow-none dark:ring-1 dark:ring-white/5"
            in:fly={{ y: 30, duration: 600, delay: 350, easing: cubicOut }}
          >
            <div class="mb-4 flex items-center justify-between">
              <div>
                <h2 class="flex items-center gap-2 text-lg font-semibold">
                  <Activity class="h-5 w-5 text-muted-foreground" />
                  Your Typical Day
                </h2>
                <p class="text-sm text-muted-foreground">
                  Glucose pattern over 24 hours
                </p>
              </div>
              <Button
                href="/reports/agp"
                variant="ghost"
                size="sm"
                class="gap-1.5"
              >
                Full Report
                <ArrowRight class="h-4 w-4" />
              </Button>
            </div>
            <div class="h-56">
              <AmbulatoryGlucoseProfile {averagedStats} />
            </div>
          </div>
        {:else if !isLoading}
          <!-- No Data State -->
          <div
            class="rounded-3xl bg-white p-12 text-center shadow-lg dark:bg-slate-900"
            in:fade={{ duration: 400 }}
          >
            <div
              class="mx-auto mb-4 flex h-20 w-20 items-center justify-center rounded-full bg-amber-100 dark:bg-amber-900/30"
            >
              <AlertTriangle class="h-10 w-10 text-amber-500" />
            </div>
            <h2 class="mb-2 text-xl font-semibold">No Data Available</h2>
            <p class="mx-auto max-w-md text-muted-foreground">
              There aren't enough glucose readings in the selected date range to
              generate analytics. Try selecting a larger date range.
            </p>
          </div>
        {/if}
      </div>
    </section>

    <!-- Quick Actions -->
    <section class="container mx-auto max-w-6xl px-4 py-6">
      <div
        class="flex flex-wrap items-center justify-center gap-3"
        in:fly={{ y: 20, duration: 500, delay: 450, easing: cubicOut }}
      >
        <Button
          href="/reports/executive-summary"
          class="gap-2 rounded-full px-5"
        >
          <Gauge class="h-4 w-4" />
          Executive Summary
        </Button>
        <Button
          href="/reports/agp"
          variant="outline"
          class="gap-2 rounded-full px-5"
        >
          <BarChart3 class="h-4 w-4" />
          AGP Report
        </Button>
        <Button
          href="/reports/readings"
          variant="outline"
          class="gap-2 rounded-full px-5"
        >
          <Calendar class="h-4 w-4" />
          Day-by-Day
        </Button>
      </div>
    </section>

    <!-- Report Categories -->
    <section class="container mx-auto max-w-6xl px-4 pb-16 pt-8">
      <div
        class="mb-10 text-center"
        in:fly={{ y: 20, duration: 500, delay: 500, easing: cubicOut }}
      >
        <h2 class="text-3xl font-bold tracking-tight">Explore Your Data</h2>
        <p class="mt-2 text-muted-foreground">
          Dive deeper into specific aspects of your diabetes management
        </p>
      </div>

      <div class="grid gap-6 md:grid-cols-2">
        {#each reportCategories as category, categoryIndex}
          {@const CategoryIcon = category.icon}
          {@const styles = categoryVariants({
            category: category.id as CategoryType,
          })}
          <div
            class={styles.card()}
            style="animation-delay: {staggerDelay(categoryIndex)}ms"
            in:fly={{
              y: 40,
              duration: 600,
              delay: staggerDelay(categoryIndex),
              easing: cubicOut,
            }}
          >
            <div class="p-6">
              <!-- Category Header -->
              <div class="mb-5 flex items-start gap-4">
                <div class={styles.iconWrap()}>
                  <CategoryIcon class={styles.icon()} />
                </div>
                <div class="flex-1">
                  <h3 class={styles.title()}>{category.title}</h3>
                  <p class={styles.subtitle()}>{category.subtitle}</p>
                </div>
              </div>

              <!-- Reports List -->
              <div class="space-y-2">
                {#each category.reports as report}
                  {@const ReportIcon = report.icon}
                  {#if report.status === "available"}
                    <a
                      href={report.href}
                      class="group/report flex items-center gap-3 rounded-xl bg-white/60 p-3 transition-all hover:bg-white hover:shadow-md dark:bg-white/5 dark:hover:bg-white/10"
                    >
                      <div
                        class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-white shadow-sm dark:bg-slate-800"
                      >
                        <ReportIcon
                          class="h-5 w-5 text-slate-600 dark:text-slate-300"
                        />
                      </div>
                      <div class="min-w-0 flex-1">
                        <div
                          class="font-medium text-slate-900 dark:text-slate-100"
                        >
                          {report.title}
                        </div>
                        <div
                          class="truncate text-sm text-slate-500 dark:text-slate-400"
                        >
                          {report.description}
                        </div>
                      </div>
                      <ChevronRight
                        class="h-5 w-5 text-slate-400 transition-transform group-hover/report:translate-x-0.5"
                      />
                    </a>
                  {:else}
                    <div
                      class="flex items-center gap-3 rounded-xl bg-white/30 p-3 opacity-60 dark:bg-white/5"
                    >
                      <div
                        class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-white/50 dark:bg-slate-800/50"
                      >
                        <ReportIcon
                          class="h-5 w-5 text-slate-400 dark:text-slate-500"
                        />
                      </div>
                      <div class="min-w-0 flex-1">
                        <div
                          class="font-medium text-slate-500 dark:text-slate-400"
                        >
                          {report.title}
                        </div>
                        <div class="text-sm text-slate-400 dark:text-slate-500">
                          Coming soon
                        </div>
                      </div>
                    </div>
                  {/if}
                {/each}
              </div>
            </div>
          </div>
        {/each}
      </div>
    </section>

    <!-- Footer Note -->
    <section class="container mx-auto max-w-6xl px-4 pb-12">
      <div
        class="rounded-2xl bg-slate-50 p-6 text-center dark:bg-slate-900/50"
        in:fade={{ duration: 400, delay: 800 }}
      >
        <p class="text-sm text-muted-foreground">
          <span class="font-medium">
            {entries.length.toLocaleString()} readings
          </span>
          from {new Date(dateRange.from).toLocaleDateString()} to {new Date(
            dateRange.to
          ).toLocaleDateString()}
          <span class="mx-2 opacity-50">•</span>
          Last updated {new Date(dateRange.lastUpdated).toLocaleTimeString([], {
            hour: "2-digit",
            minute: "2-digit",
          })}
        </p>
        <p class="mt-1 text-xs text-muted-foreground/70">
          This report is for informational purposes. Always consult your
          healthcare provider for medical advice.
        </p>
      </div>
    </section>
  </div>
{/if}
