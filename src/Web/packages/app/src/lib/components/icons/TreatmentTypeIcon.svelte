<script lang="ts">
  import type { IconProps } from "./types";
  import Syringe from "lucide-svelte/icons/syringe";
  import Utensils from "lucide-svelte/icons/utensils";
  import Activity from "lucide-svelte/icons/activity";
  import Smartphone from "lucide-svelte/icons/smartphone";
  import HeartPulse from "lucide-svelte/icons/heart-pulse";
  import Bell from "lucide-svelte/icons/bell";
  import MessageSquare from "lucide-svelte/icons/message-square";
  import HelpCircle from "lucide-svelte/icons/help-circle";
  import Dog from "lucide-svelte/icons/dog";
  import { TREATMENT_CATEGORIES } from "$lib/constants/treatment-categories";

  interface Props extends IconProps {
    eventType?: string | null;
    /** Optional override color - if not provided, uses category color */
    color?: string;
  }

  let { eventType, class: className = "", color, ...rest }: Props = $props();

  /** Get the icon component for a given event type */
  function getIconComponent(type: string | null | undefined) {
    if (!type) return Activity;

    // Bolus & Insulin
    if (
      type.includes("Bolus") ||
      type === "SMB" ||
      type === "Correction Bolus"
    ) {
      return Syringe;
    }

    // Carbs & Meals
    if (type === "Carb Correction" || type === "Meal" || type === "Snack") {
      return Utensils;
    }

    // Basal & Profiles
    if (type.includes("Basal") || type === "Profile Switch") {
      return Activity;
    }

    // Device Events
    if (
      type.includes("Sensor") ||
      type.includes("Site") ||
      type.includes("Insulin Change") ||
      type.includes("Battery")
    ) {
      return Smartphone;
    }

    // Notes & Alerts
    if (type === "Note") return MessageSquare;
    if (type === "Announcement") return Bell;
    if (type === "Question") return HelpCircle;
    if (type === "D.A.D. Alert") return Dog;
    if (type === "BG Check") return HeartPulse;

    // Fallback
    return Activity;
  }

  /** Get the category color class for an event type */
  function getCategoryColorClass(type: string | null | undefined): string {
    if (!type) return "text-muted-foreground";

    for (const category of Object.values(TREATMENT_CATEGORIES)) {
      if (category.eventTypes.some((et) => et === type)) {
        return category.colorClass;
      }
    }

    return "text-muted-foreground";
  }

  const IconComponent = $derived(getIconComponent(eventType));
  const colorClass = $derived(color ? "" : getCategoryColorClass(eventType));
</script>

<!--
  TreatmentTypeIcon - renders the appropriate icon for a treatment event type.
  Icon selection is based on the event type string.
  Color is determined by the treatment category unless overridden.
-->
<IconComponent
  class="{colorClass} {className}"
  style={color ? `color: ${color}` : undefined}
  {...rest}
/>
