<script lang="ts">
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import type { InjectableMedication, InjectableDose } from "$lib/api";
  import { Pill, Clock, AlertTriangle } from "lucide-svelte";
  import {
    getActiveMedications,
    getRecentDoses,
  } from "$lib/data/medications.remote";

  // Query data
  const medicationsQuery = $derived(getActiveMedications());
  const dosesQuery = $derived(getRecentDoses());

  // Combine medications with their latest dose info
  interface MedicationStatus {
    medication: InjectableMedication;
    lastDose: InjectableDose | null;
    hoursAgo: number | null;
    isOverdue: boolean;
  }

  let medicationStatuses = $derived.by(() => {
    const meds = medicationsQuery.current;
    const doses = dosesQuery.current;
    if (!meds || !doses) return [];

    // Filter to non-rapid medications (long-acting, GLP-1, etc.)
    const nonRapidCategories = [
      "LongActing",
      "UltraLong",
      "Intermediate",
      "GLP1Daily",
      "GLP1Weekly",
    ];

    const relevantMeds = meds.filter((m: InjectableMedication) =>
      nonRapidCategories.includes(m.category ?? "")
    );

    const now = Date.now();
    const statuses: MedicationStatus[] = relevantMeds.map(
      (med: InjectableMedication) => {
        // Find most recent dose for this medication
        const medDoses = doses
          .filter(
            (d: InjectableDose) => d.injectableMedicationId === med.id
          )
          .sort(
            (a: InjectableDose, b: InjectableDose) =>
              (b.timestamp ?? 0) - (a.timestamp ?? 0)
          );

        const lastDose = medDoses.length > 0 ? medDoses[0] : null;
        const hoursAgo = lastDose?.timestamp
          ? (now - lastDose.timestamp) / (1000 * 60 * 60)
          : null;

        // Determine expected interval based on category
        let expectedIntervalHours = 24;
        if (med.category === "GLP1Weekly") expectedIntervalHours = 168;
        else if (med.duration) expectedIntervalHours = med.duration;

        // Overdue if more than 10% past expected interval
        const isOverdue =
          hoursAgo !== null && hoursAgo > expectedIntervalHours * 1.1;

        return { medication: med, lastDose, hoursAgo, isOverdue };
      }
    );

    // Sort: overdue first, then by hours since last dose descending
    return statuses.sort((a, b) => {
      if (a.isOverdue && !b.isOverdue) return -1;
      if (!a.isOverdue && b.isOverdue) return 1;
      return (b.hoursAgo ?? Infinity) - (a.hoursAgo ?? Infinity);
    });
  });

  function formatHoursAgo(hours: number | null): string {
    if (hours === null) return "No doses recorded";
    if (hours < 1) return `${Math.round(hours * 60)}min ago`;
    if (hours < 24) return `${Math.round(hours)}h ago`;
    const days = Math.floor(hours / 24);
    const remainingHours = Math.round(hours % 24);
    if (days === 1) return `1d ${remainingHours}h ago`;
    return `${days}d ${remainingHours}h ago`;
  }
</script>

{#await Promise.all([medicationsQuery, dosesQuery])}
  <Card>
    <CardHeader class="pb-3">
      <CardTitle class="text-sm font-medium flex items-center gap-2">
        <Pill class="h-4 w-4" />
        Active Medications
      </CardTitle>
    </CardHeader>
    <CardContent>
      <div class="animate-pulse text-sm text-muted-foreground">Loading...</div>
    </CardContent>
  </Card>
{:then}
  {#if medicationStatuses.length > 0}
    <Card>
      <CardHeader class="pb-3">
        <CardTitle class="text-sm font-medium flex items-center gap-2">
          <Pill class="h-4 w-4" />
          Active Medications
        </CardTitle>
      </CardHeader>
      <CardContent class="space-y-2">
        {#each medicationStatuses as status}
          <div
            class="flex items-center justify-between py-1.5 {status.isOverdue
              ? 'text-destructive'
              : ''}"
          >
            <div class="flex items-center gap-2 min-w-0 flex-1">
              {#if status.isOverdue}
                <AlertTriangle class="h-3.5 w-3.5 shrink-0 text-destructive" />
              {:else}
                <Clock class="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
              {/if}
              <span class="text-sm truncate">
                {status.medication.name ?? "Unknown"}
              </span>
              {#if status.lastDose?.units}
                <Badge variant="secondary" class="text-xs shrink-0">
                  {status.lastDose.units}{status.medication.unitType ===
                  "Milligrams"
                    ? "mg"
                    : "u"}
                </Badge>
              {/if}
            </div>
            <span
              class="text-xs shrink-0 ml-2 {status.isOverdue
                ? 'text-destructive font-medium'
                : 'text-muted-foreground'}"
            >
              {formatHoursAgo(status.hoursAgo)}
            </span>
          </div>
        {/each}
      </CardContent>
    </Card>
  {/if}
{:catch}
  <!-- Silently fail - this is a supplementary panel -->
{/await}
