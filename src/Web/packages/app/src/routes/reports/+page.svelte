<script lang="ts" module>
  import { tv, type VariantProps } from "tailwind-variants";

  export const categoryVariants = tv({
    slots: {
      card: "border",
      header: "rounded-t-xl",
      icon: "h-6 w-6",
      subtitle: "mt-1 text-sm font-medium",
      reportIcon: "h-5 w-5",
      reportBg: "flex h-10 w-10 items-center justify-center rounded-lg",
    },
    variants: {
      category: {
        overview: {
          card: "border-border",
          header: "bg-primary/5",
          icon: "text-primary",
          subtitle: "text-primary",
          reportIcon: "text-primary",
          reportBg: "bg-primary/10",
        },
        patterns: {
          card: "border-border",
          header: "bg-violet-500/5 dark:bg-violet-500/10",
          icon: "text-violet-600 dark:text-violet-400",
          subtitle: "text-violet-600 dark:text-violet-400",
          reportIcon: "text-violet-600 dark:text-violet-400",
          reportBg: "bg-violet-500/10",
        },
        lifestyle: {
          card: "border-border",
          header: "bg-emerald-500/5 dark:bg-emerald-500/10",
          icon: "text-emerald-600 dark:text-emerald-400",
          subtitle: "text-emerald-600 dark:text-emerald-400",
          reportIcon: "text-emerald-600 dark:text-emerald-400",
          reportBg: "bg-emerald-500/10",
        },
        treatment: {
          card: "border-border",
          header: "bg-amber-500/5 dark:bg-amber-500/10",
          icon: "text-amber-600 dark:text-amber-400",
          subtitle: "text-amber-600 dark:text-amber-400",
          reportIcon: "text-amber-600 dark:text-amber-400",
          reportBg: "bg-amber-500/10",
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
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import { Separator } from "$lib/components/ui/separator";
  import {
    Tabs,
    TabsContent,
    TabsList,
    TabsTrigger,
  } from "$lib/components/ui/tabs";
  import { cn } from "$lib/utils";
  import {
    Activity,
    TrendingUp,
    Target,
    BarChart3,
    Clock,
    Calendar,
    FileText,
    Gauge,
    AlertTriangle,
    Moon,
    Utensils,
    Dumbbell,
    Shield,
    BookOpen,
    ChartColumn,
    Heart,
    Stethoscope,
    ArrowRight,
    Printer,
  } from "lucide-svelte";
  import TIRStackedChart from "$lib/components/reports/TIRStackedChart.svelte";
  import { AmbulatoryGlucoseProfile } from "$lib/components/ambulatory-glucose-profile";
  import { GlucoseChart } from "$lib/components/glucose-chart";
  import GlucoseScoreCard from "$lib/components/reports/GlucoseScoreCard.svelte";
  import ClinicalInsights from "$lib/components/reports/ClinicalInsights.svelte";
  import type { ScoreCardStatus } from "$lib/components/reports/GlucoseScoreCard.svelte";

  let { data } = $props();

  // Helper functions for status determination
  function getTIRStatus(tir: number): ScoreCardStatus {
    if (tir >= 70) return "excellent";
    if (tir >= 60) return "good";
    if (tir >= 50) return "fair";
    if (tir >= 40) return "needs-attention";
    return "critical";
  }

  function getCVStatus(cv: number): ScoreCardStatus {
    if (cv <= 33) return "excellent";
    if (cv <= 36) return "good";
    if (cv <= 40) return "fair";
    if (cv <= 50) return "needs-attention";
    return "critical";
  }

  function getA1cStatus(a1c: number): ScoreCardStatus {
    if (a1c < 6.5) return "excellent";
    if (a1c < 7.0) return "good";
    if (a1c < 7.5) return "fair";
    if (a1c < 8.5) return "needs-attention";
    return "critical";
  }

  function getLowStatus(lowPercent: number): ScoreCardStatus {
    if (lowPercent < 1) return "excellent";
    if (lowPercent < 4) return "good";
    if (lowPercent < 6) return "fair";
    if (lowPercent < 10) return "needs-attention";
    return "critical";
  }

  // Report navigation categories with user-friendly descriptions
  const reportCategories: Array<{
    id: NonNullable<CategoryType>;
    title: string;
    subtitle: string;
    description: string;
    icon: typeof Gauge;
    reports: Array<{
      title: string;
      userDescription: string;
      clinicalDescription: string;
      href: string;
      icon: typeof Gauge;
      status: "available" | "coming-soon";
      forClinic: boolean;
    }>;
  }> = [
    {
      id: "overview",
      title: "The Big Picture",
      subtitle: "How am I doing overall?",
      description:
        "Get a comprehensive snapshot of your diabetes management with key metrics that matter most.",
      icon: Gauge,
      reports: [
        {
          title: "Executive Summary",
          userDescription:
            "Your most important numbers at a glance — perfect for quick check-ins or sharing with your doctor.",
          clinicalDescription:
            "HbA1c estimate, TIR, CV, and glycemic variability indices with trend analysis.",
          href: "/reports/executive-summary",
          icon: Gauge,
          status: "available",
          forClinic: true,
        },
        {
          title: "Glucose Profile (AGP)",
          userDescription:
            "See your typical day's glucose pattern — when you tend to run high, low, or in range.",
          clinicalDescription:
            "Standardized AGP with 14-day overlay, percentile bands (10th-90th), and daily profiles.",
          href: "/reports/agp",
          icon: BarChart3,
          status: "available",
          forClinic: true,
        },
      ],
    },
    {
      id: "patterns",
      title: "Patterns & Trends",
      subtitle: "What's affecting my glucose?",
      description:
        "Discover what makes your glucose go up, down, or stay stable throughout the day.",
      icon: TrendingUp,
      reports: [
        {
          title: "Time in Range",
          userDescription:
            "Track how much time you spend in your target range and see your progress over time.",
          clinicalDescription:
            "Detailed TIR breakdown by range with episode counts, durations, and trend analysis.",
          href: "/reports/time-in-range",
          icon: Target,
          status: "coming-soon",
          forClinic: false,
        },
        {
          title: "Hourly Patterns",
          userDescription:
            "Find out which hours of the day are your best — and which need more attention.",
          clinicalDescription:
            "Hourly glucose percentiles with statistical analysis for circadian pattern identification.",
          href: "/reports/hourly-stats",
          icon: Clock,
          status: "coming-soon",
          forClinic: false,
        },
        {
          title: "Day-by-Day View",
          userDescription:
            "Review each day individually to spot what worked and what didn't.",
          clinicalDescription:
            "Daily glucose overlay with treatment markers, statistics, and pattern comparison.",
          href: "/reports/readings",
          icon: Calendar,
          status: "available",
          forClinic: false,
        },
      ],
    },
    {
      id: "lifestyle",
      title: "Lifestyle Impact",
      subtitle: "How do food, exercise & sleep affect me?",
      description:
        "Understand how your daily activities influence your glucose levels.",
      icon: Heart,
      reports: [
        {
          title: "Meal Analysis",
          userDescription:
            "See how different meals affect your glucose — find what works best for you.",
          clinicalDescription:
            "Pre/post meal glucose analysis with I:C ratio effectiveness and meal timing optimization.",
          href: "/reports/meals",
          icon: Utensils,
          status: "coming-soon",
          forClinic: false,
        },
        {
          title: "Exercise Impact",
          userDescription:
            "Discover how physical activity affects your glucose control.",
          clinicalDescription:
            "Exercise correlation analysis with glucose delta, timing impact, and recommendations.",
          href: "/reports/exercise",
          icon: Dumbbell,
          status: "coming-soon",
          forClinic: false,
        },
        {
          title: "Sleep & Overnight",
          userDescription:
            "Track your glucose patterns during sleep and wake up with better numbers.",
          clinicalDescription:
            "Overnight glucose analysis with dawn phenomenon detection and nocturnal hypo risk.",
          href: "/reports/sleep",
          icon: Moon,
          status: "coming-soon",
          forClinic: false,
        },
      ],
    },
    {
      id: "treatment",
      title: "Treatment Insights",
      subtitle: "Is my treatment working?",
      description:
        "Evaluate how well your medications and insulin are working for you.",
      icon: Stethoscope,
      reports: [
        {
          title: "Treatment Log",
          userDescription:
            "Review your insulin doses, carbs, and corrections all in one place.",
          clinicalDescription:
            "Comprehensive treatment log with bolus/basal breakdown, IOB tracking, and dosing patterns.",
          href: "/reports/treatments",
          icon: FileText,
          status: "available",
          forClinic: true,
        },
        {
          title: "Correction Analysis",
          userDescription:
            "See if your correction doses are bringing you back to range effectively.",
          clinicalDescription:
            "ISF validation, correction factor analysis, and time-to-target metrics.",
          href: "/reports/corrections",
          icon: Target,
          status: "coming-soon",
          forClinic: true,
        },
      ],
    },
  ];
</script>

<svelte:head>
  <title>Reports - Nocturne</title>
  <meta
    name="description"
    content="Comprehensive diabetes management analytics and insights"
  />
</svelte:head>

<div class="container mx-auto space-y-8 px-4 py-6">
  <!-- Welcome Header with Context -->
  <div class="space-y-3 text-center">
    <div
      class="flex items-center justify-center gap-2 text-sm text-muted-foreground"
    >
      <Calendar class="h-4 w-4" />
      <span>
        {new Date(data.dateRange.from).toLocaleDateString()} – {new Date(
          data.dateRange.to
        ).toLocaleDateString()}
      </span>
      <span class="text-muted-foreground/50">•</span>
      <span>{data.entries.length.toLocaleString()} readings</span>
    </div>
    <h1 class="text-4xl font-bold">Your Glucose Report</h1>
    <p class="mx-auto max-w-2xl text-lg text-muted-foreground">
      Everything you and your healthcare team need to understand your diabetes
      management.
    </p>
  </div>

  <!-- Quick Actions Bar -->
  <div class="flex flex-wrap items-center justify-center gap-2">
    <Button href="/reports/executive-summary" size="sm" class="gap-2">
      <Gauge class="h-4 w-4" />
      Executive Summary
    </Button>
    <Button href="/reports/agp" size="sm" variant="outline" class="gap-2">
      <BarChart3 class="h-4 w-4" />
      AGP Report
    </Button>
    <Button href="/reports/readings" size="sm" variant="outline" class="gap-2">
      <Calendar class="h-4 w-4" />
      Day-by-Day
    </Button>
    <Button
      size="sm"
      variant="ghost"
      class="gap-2"
      onclick={() => window.print()}
    >
      <Printer class="h-4 w-4" />
      Print
    </Button>
  </div>

  <!-- Main Dashboard Section -->
  {#await data.analysis then analysis}
    {@const tir = analysis?.timeInRange?.percentages}
    {@const variability = analysis?.glycemicVariability}
    {@const stats = analysis?.basicStats}
    {@const quality = analysis?.dataQuality}

    <!-- Score Cards - The Key Numbers -->
    <div class="grid grid-cols-1 gap-4 md:grid-cols-2 lg:grid-cols-4">
      <GlucoseScoreCard
        title="Time in Range"
        value={tir?.target?.toFixed(0) ?? "–"}
        unit="%"
        status={getTIRStatus(tir?.target ?? 0)}
        explanation="This is how much time your glucose stays in your target zone (70-180 mg/dL). Higher is better!"
        clinicalContext="ADA recommends >70% TIR for most adults with diabetes. Each 5% improvement is clinically meaningful."
        targetRange={{ optimal: 70 }}
        colorClass="text-green-600"
      >
        {#snippet icon()}
          <Target class="h-5 w-5 text-green-600" />
        {/snippet}
      </GlucoseScoreCard>

      <GlucoseScoreCard
        title="Estimated A1C"
        value={variability?.estimatedA1c?.toFixed(1) ?? "–"}
        unit="%"
        status={getA1cStatus(variability?.estimatedA1c ?? 8)}
        explanation="This estimates your 3-month average blood sugar. It's based on your CGM data from this period."
        clinicalContext="eA1C calculated using Nathan formula from GMI. Lab A1C may differ due to red blood cell lifespan variations."
        targetRange={{ max: 7.0 }}
        colorClass="text-red-600"
      >
        {#snippet icon()}
          <Gauge class="h-5 w-5 text-red-600" />
        {/snippet}
      </GlucoseScoreCard>

      <GlucoseScoreCard
        title="Glucose Variability"
        value={variability?.coefficientOfVariation?.toFixed(0) ?? "–"}
        unit="% CV"
        status={getCVStatus(variability?.coefficientOfVariation ?? 40)}
        explanation="Lower variability means steadier glucose levels with fewer ups and downs. This affects how you feel day-to-day."
        clinicalContext="CV ≤33% indicates stable glucose. CV >36% associated with increased hypoglycemia risk."
        targetRange={{ max: 33 }}
        colorClass="text-purple-600"
      >
        {#snippet icon()}
          <TrendingUp class="h-5 w-5 text-purple-600" />
        {/snippet}
      </GlucoseScoreCard>

      <GlucoseScoreCard
        title="Time Below Range"
        value={((tir?.low ?? 0) + (tir?.severeLow ?? 0)).toFixed(1)}
        unit="%"
        status={getLowStatus((tir?.low ?? 0) + (tir?.severeLow ?? 0))}
        explanation="Time spent with low blood sugar. Less is better — lows can be dangerous and feel awful."
        clinicalContext="Target <4% time below 70 mg/dL and <1% below 54 mg/dL. Prioritize reducing lows, especially in elderly."
        targetRange={{ max: 4 }}
        colorClass="text-red-500"
      >
        {#snippet icon()}
          <AlertTriangle class="h-5 w-5 text-red-500" />
        {/snippet}
      </GlucoseScoreCard>
    </div>

    <!-- Visual Dashboard Row -->
    <div class="grid grid-cols-1 gap-6 lg:grid-cols-3">
      <!-- Time in Range Visualization -->
      <Card class="border lg:col-span-1">
        <CardHeader>
          <CardTitle class="flex items-center gap-2 text-lg">
            <ChartColumn class="h-5 w-5 text-muted-foreground" />
            Time in Range
          </CardTitle>
          <CardDescription>Where you spend your time</CardDescription>
        </CardHeader>
        <CardContent class="flex justify-center">
          <TIRStackedChart entries={data.entries} />
        </CardContent>
      </Card>

      <!-- AGP Preview -->
      <Card class="border lg:col-span-2">
        <CardHeader>
          <div class="flex items-center justify-between">
            <div>
              <CardTitle class="flex items-center gap-2 text-lg">
                <BarChart3 class="h-5 w-5 text-muted-foreground" />
                Your Typical Day
              </CardTitle>
              <CardDescription>
                Glucose pattern over 24 hours (darker = more common)
              </CardDescription>
            </div>
            <Button
              href="/reports/agp"
              variant="outline"
              size="sm"
              class="gap-1"
            >
              Full Report
              <ArrowRight class="h-4 w-4" />
            </Button>
          </div>
        </CardHeader>
        <CardContent class="h-64">
          <AmbulatoryGlucoseProfile entries={data.entries} />
        </CardContent>
      </Card>
    </div>

    <!-- AI Insights Section -->
    <ClinicalInsights {analysis} showClinicalNotes={true} maxInsights={4} />

    <!-- Recent Glucose Preview -->
    <Card class="border">
      <CardHeader>
        <div class="flex items-center justify-between">
          <div>
            <CardTitle class="flex items-center gap-2 text-lg">
              <Activity class="h-5 w-5 text-muted-foreground" />
              Recent Glucose
            </CardTitle>
            <CardDescription>
              Your glucose levels over the selected period
            </CardDescription>
          </div>
          <Button
            href="/reports/readings"
            variant="outline"
            size="sm"
            class="gap-1"
          >
            Day-by-Day View
            <ArrowRight class="h-4 w-4" />
          </Button>
        </div>
      </CardHeader>
      <CardContent class="h-72 md:h-96">
        <GlucoseChart
          entries={data.entries}
          treatments={data.treatments}
          dateRange={{
            from: new Date(data.dateRange.from),
            to: new Date(data.dateRange.to),
          }}
        />
      </CardContent>
    </Card>

    <!-- Additional Statistics Grid -->
    <div class="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-6">
      <Card class="border bg-card p-4 text-center">
        <div class="text-2xl font-bold tabular-nums">{stats?.mean?.toFixed(0) ?? "–"}</div>
        <div class="text-xs font-medium text-muted-foreground">Average</div>
        <div class="text-[10px] text-muted-foreground/60">mg/dL</div>
      </Card>
      <Card class="border bg-card p-4 text-center">
        <div class="text-2xl font-bold tabular-nums">{stats?.median?.toFixed(0) ?? "–"}</div>
        <div class="text-xs font-medium text-muted-foreground">Median</div>
        <div class="text-[10px] text-muted-foreground/60">mg/dL</div>
      </Card>
      <Card class="border bg-card p-4 text-center">
        <div class="text-2xl font-bold tabular-nums">{stats?.min?.toFixed(0) ?? "–"}</div>
        <div class="text-xs font-medium text-muted-foreground">Lowest</div>
        <div class="text-[10px] text-muted-foreground/60">mg/dL</div>
      </Card>
      <Card class="border bg-card p-4 text-center">
        <div class="text-2xl font-bold tabular-nums">{stats?.max?.toFixed(0) ?? "–"}</div>
        <div class="text-xs font-medium text-muted-foreground">Highest</div>
        <div class="text-[10px] text-muted-foreground/60">mg/dL</div>
      </Card>
      <Card class="border bg-card p-4 text-center">
        <div class="text-2xl font-bold tabular-nums">
          {stats?.standardDeviation?.toFixed(0) ?? "–"}
        </div>
        <div class="text-xs font-medium text-muted-foreground">Std Dev</div>
        <div class="text-[10px] text-muted-foreground/60">mg/dL</div>
      </Card>
      <Card class="border bg-card p-4 text-center">
        <div class="text-2xl font-bold tabular-nums">
          {quality?.cgmActivePercent?.toFixed(0) ?? "–"}%
        </div>
        <div class="text-xs font-medium text-muted-foreground">CGM Active</div>
        <div class="text-[10px] text-muted-foreground/60">data quality</div>
      </Card>
    </div>
  {:catch error}
    <Card class="border border-destructive/50 bg-destructive/5">
      <CardHeader>
        <CardTitle class="flex items-center gap-2 text-destructive">
          <AlertTriangle class="h-5 w-5" />
          Error Loading Analytics
        </CardTitle>
      </CardHeader>
      <CardContent class="space-y-3">
        <p class="text-sm text-muted-foreground">
          There was an error generating your analytics report. This usually
          means there is not enough data in the selected time range to perform
          the necessary calculations.
        </p>
        <p class="text-sm text-muted-foreground">
          Please select a larger date range or ensure you have sufficient
          glucose readings.
        </p>
        <pre
          class="overflow-auto rounded-md border bg-muted/50 p-3 text-xs">{error.message}</pre>
      </CardContent>
    </Card>
  {/await}

  <Separator class="my-8" />

  <!-- Report Categories - Tabbed Interface -->
  <div class="space-y-6">
    <div class="text-center">
      <h2 class="text-2xl font-bold">Explore Detailed Reports</h2>
      <p class="text-muted-foreground">
        Dive deeper into specific aspects of your diabetes management
      </p>
    </div>

    <Tabs value="overview" class="w-full">
      <TabsList class="grid h-auto w-full grid-cols-2 md:grid-cols-4">
        {#each reportCategories as category}
          {@const CategoryIcon = category.icon}
          <TabsTrigger value={category.id} class="gap-2 py-3">
            <CategoryIcon class="h-4 w-4" />
            <span class="hidden sm:inline">{category.title}</span>
            <span class="sm:hidden">{category.title.split(" ")[0]}</span>
          </TabsTrigger>
        {/each}
      </TabsList>

      {#each reportCategories as category}
        {@const CategoryIcon = category.icon}
        {@const styles = categoryVariants({ category: category.id })}
        <TabsContent value={category.id} class="mt-6">
          <Card class={styles.card()}>
            <CardHeader class={styles.header()}>
              <div class="flex items-start justify-between">
                <div class="space-y-1">
                  <CardTitle class="flex items-center gap-2 text-xl">
                    <CategoryIcon class={styles.icon()} />
                    {category.title}
                  </CardTitle>
                  <p class={styles.subtitle()}>
                    {category.subtitle}
                  </p>
                  <CardDescription class="pt-1">
                    {category.description}
                  </CardDescription>
                </div>
              </div>
            </CardHeader>
            <CardContent class="pt-6">
              <div class="grid grid-cols-1 gap-4 md:grid-cols-2 lg:grid-cols-3">
                {#each category.reports as report}
                  {@const ReportIcon = report.icon}
                  <Card
                    class={cn(
                      "group relative overflow-hidden border transition-all duration-200",
                      report.status === "available"
                        ? "hover:border-primary hover:shadow-md"
                        : "opacity-60"
                    )}
                  >
                    <CardHeader class="space-y-3 pb-3">
                      <div class="flex items-start justify-between gap-2">
                        <div class={styles.reportBg()}>
                          <ReportIcon class={styles.reportIcon()} />
                        </div>
                        <div class="flex flex-wrap items-center justify-end gap-1.5">
                          {#if report.forClinic}
                            <Badge variant="outline" class="text-[10px]">
                              <Stethoscope class="mr-1 h-3 w-3" />
                              Clinical
                            </Badge>
                          {/if}
                          <Badge
                            variant={report.status === "available"
                              ? "default"
                              : "secondary"}
                            class="text-[10px]"
                          >
                            {report.status === "available"
                              ? "Available"
                              : "Coming Soon"}
                          </Badge>
                        </div>
                      </div>
                      <CardTitle class="text-base leading-tight">
                        {report.title}
                      </CardTitle>
                    </CardHeader>
                    <CardContent class="space-y-4 pt-0">
                      <p class="text-sm leading-relaxed text-muted-foreground">
                        {report.userDescription}
                      </p>

                      <details class="group/clinical">
                        <summary
                          class="flex cursor-pointer items-center gap-1.5 text-xs text-muted-foreground hover:text-foreground"
                        >
                          <Stethoscope class="h-3 w-3" />
                          <span>Clinical details</span>
                        </summary>
                        <p
                          class="mt-2 rounded-md border bg-muted/30 p-2.5 text-xs leading-relaxed text-muted-foreground"
                        >
                          {report.clinicalDescription}
                        </p>
                      </details>

                      {#if report.status === "available"}
                        <Button
                          href={report.href}
                          size="sm"
                          class="w-full gap-2"
                        >
                          View Report
                          <ArrowRight class="h-4 w-4" />
                        </Button>
                      {:else}
                        <Button
                          disabled
                          size="sm"
                          variant="secondary"
                          class="w-full"
                        >
                          Coming Soon
                        </Button>
                      {/if}
                    </CardContent>
                  </Card>
                {/each}
              </div>
            </CardContent>
          </Card>
        </TabsContent>
      {/each}
    </Tabs>
  </div>

  <!-- Understanding Your Reports - Educational Section -->
  <Card class="border bg-muted/30">
    <CardHeader>
      <CardTitle class="flex items-center gap-2">
        <BookOpen class="h-5 w-5 text-primary" />
        Understanding Your Reports
      </CardTitle>
      <CardDescription>
        Quick guide to interpreting your diabetes data
      </CardDescription>
    </CardHeader>
    <CardContent>
      <div class="grid grid-cols-1 gap-6 sm:grid-cols-2 lg:grid-cols-4">
        <div class="space-y-2">
          <div class="flex items-center gap-2">
            <div class="flex h-8 w-8 items-center justify-center rounded-lg bg-emerald-500/10">
              <Target class="h-4 w-4 text-emerald-600 dark:text-emerald-400" />
            </div>
            <h3 class="font-semibold">Time in Range</h3>
          </div>
          <p class="text-sm text-muted-foreground">
            The percentage of time your glucose is between 70-180 mg/dL.
            <span class="font-medium text-foreground">Goal: 70%+</span>
          </p>
        </div>
        <div class="space-y-2">
          <div class="flex items-center gap-2">
            <div class="flex h-8 w-8 items-center justify-center rounded-lg bg-rose-500/10">
              <Gauge class="h-4 w-4 text-rose-600 dark:text-rose-400" />
            </div>
            <h3 class="font-semibold">Estimated A1C</h3>
          </div>
          <p class="text-sm text-muted-foreground">
            A calculation of your average glucose over time, similar to the lab
            test.
            <span class="font-medium text-foreground">Goal: Below 7%</span>
          </p>
        </div>
        <div class="space-y-2">
          <div class="flex items-center gap-2">
            <div class="flex h-8 w-8 items-center justify-center rounded-lg bg-violet-500/10">
              <TrendingUp class="h-4 w-4 text-violet-600 dark:text-violet-400" />
            </div>
            <h3 class="font-semibold">Variability (CV)</h3>
          </div>
          <p class="text-sm text-muted-foreground">
            Measures how much your glucose swings up and down.
            <span class="font-medium text-foreground">Goal: 33% or lower</span>
          </p>
        </div>
        <div class="space-y-2">
          <div class="flex items-center gap-2">
            <div class="flex h-8 w-8 items-center justify-center rounded-lg bg-sky-500/10">
              <Shield class="h-4 w-4 text-sky-600 dark:text-sky-400" />
            </div>
            <h3 class="font-semibold">Data Quality</h3>
          </div>
          <p class="text-sm text-muted-foreground">
            How much of the time your CGM was actively providing readings.
            <span class="font-medium text-foreground">Goal: 90%+</span>
          </p>
        </div>
      </div>
    </CardContent>
  </Card>

  <!-- Footer -->
  <div class="space-y-1 text-center text-xs text-muted-foreground">
    <p>
      Report generated from {data.entries.length.toLocaleString()} glucose readings
      between {new Date(data.dateRange.from).toLocaleDateString()} and {new Date(
        data.dateRange.to
      ).toLocaleDateString()}
    </p>
    <p>
      Last updated: {new Date(data.dateRange.lastUpdated).toLocaleString()}
    </p>
    <p class="text-muted-foreground/60">
      This report is for informational purposes only. Always consult your
      healthcare provider for medical advice.
    </p>
  </div>
</div>
