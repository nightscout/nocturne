<script lang="ts">
  import Activity from "@lucide/svelte/icons/activity";
  import Droplet from "@lucide/svelte/icons/droplet";
  import TrendingUp from "@lucide/svelte/icons/trending-up";
  import { bgLabel } from "$lib/utils/formatting";
  import type {
    BasalSchedule,
    CarbRatioSchedule,
    SensitivitySchedule,
    TargetRangeSchedule,
  } from "$api-clients";
  import ScheduleView from "./ScheduleView.svelte";
  import TargetRangeCard from "./TargetRangeCard.svelte";

  interface Props {
    profileName: string;
    basal?: BasalSchedule | null;
    carbRatio?: CarbRatioSchedule | null;
    sensitivity?: SensitivitySchedule | null;
    targetRange?: TargetRangeSchedule | null;
    readOnly?: boolean;
  }

  let { profileName, basal, carbRatio, sensitivity, targetRange, readOnly = false }: Props =
    $props();
</script>

<div class="grid gap-4 @3xl:grid-cols-2">
  {#if basal?.entries && basal.entries.length > 0}
    <ScheduleView
      title="Basal Rates"
      description="Background insulin delivery rates"
      unit="U/hr"
      icon={Activity}
      entries={basal.entries}
    />
  {/if}

  {#if carbRatio?.entries && carbRatio.entries.length > 0}
    <ScheduleView
      title="Carb Ratios (I:C)"
      description="Grams of carbs per unit of insulin"
      unit="g/U"
      icon={Droplet}
      iconClass="text-success"
      entries={carbRatio.entries}
    />
  {/if}

  {#if sensitivity?.entries && sensitivity.entries.length > 0}
    <ScheduleView
      title="Insulin Sensitivity (ISF)"
      description="BG drop per unit of insulin"
      unit="{bgLabel()}/U"
      icon={TrendingUp}
      entries={sensitivity.entries}
      sourceUnits="mg/dl"
    />
  {/if}

  <TargetRangeCard {profileName} schedule={targetRange ?? null} {readOnly} />
</div>
