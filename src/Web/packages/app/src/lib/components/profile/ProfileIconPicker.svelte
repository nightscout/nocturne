<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import * as Popover from "$lib/components/ui/popover";
  import { cn } from "$lib/utils";
  import { PROFILE_ICONS } from "$lib/constants/profile-icons";
  import {
    User,
    UserCircle,
    Heart,
    HeartPulse,
    Activity,
    Syringe,
    Pill,
    Droplet,
    Target,
    Sun,
    Moon,
    Sunrise,
    Sunset,
    Dumbbell,
    Bike,
    Footprints,
    Utensils,
    Coffee,
    Cake,
    Baby,
    Briefcase,
    Home,
    Plane,
    Zap,
    Shield,
    Star,
    Sparkles,
    Clock,
    Calendar,
    TrendingUp,
    type Icon,
  } from "lucide-svelte";

  interface Props {
    selectedIcon: string;
    disabled?: boolean;
  }

  let { selectedIcon = $bindable("user"), disabled = false }: Props = $props();

  let open = $state(false);

  // Map icon IDs to Lucide components
  const iconComponents: Record<string, typeof Icon> = {
    user: User,
    "user-circle": UserCircle,
    heart: Heart,
    "heart-pulse": HeartPulse,
    activity: Activity,
    syringe: Syringe,
    pill: Pill,
    droplet: Droplet,
    target: Target,
    sun: Sun,
    moon: Moon,
    sunrise: Sunrise,
    sunset: Sunset,
    dumbbell: Dumbbell,
    bike: Bike,
    footprints: Footprints,
    utensils: Utensils,
    coffee: Coffee,
    cake: Cake,
    baby: Baby,
    briefcase: Briefcase,
    home: Home,
    plane: Plane,
    zap: Zap,
    shield: Shield,
    star: Star,
    sparkles: Sparkles,
    clock: Clock,
    calendar: Calendar,
    "trending-up": TrendingUp,
  };

  function selectIcon(iconId: string) {
    selectedIcon = iconId;
    open = false;
  }

  // Get current icon component
  let CurrentIcon = $derived(iconComponents[selectedIcon] ?? User);
  let currentIconName = $derived(
    PROFILE_ICONS.find((i) => i.id === selectedIcon)?.name ?? "User"
  );
</script>

<Popover.Root bind:open>
  <Popover.Trigger {disabled} class="w-full">
    <Button variant="outline" class="w-full justify-start gap-2" {disabled}>
      <CurrentIcon class="h-4 w-4" />
      <span>{currentIconName}</span>
    </Button>
  </Popover.Trigger>
  <Popover.Content class="w-80 p-3" align="start">
    <div class="space-y-2">
      <p class="text-sm font-medium">Select an icon</p>
      <div class="grid grid-cols-6 gap-2">
        {#each PROFILE_ICONS as icon}
          {@const IconComponent = iconComponents[icon.id] ?? User}
          <button
            type="button"
            class={cn(
              "flex h-9 w-9 items-center justify-center rounded-md border transition-colors hover:bg-accent",
              selectedIcon === icon.id && "border-primary bg-primary/10"
            )}
            title={icon.name}
            onclick={() => selectIcon(icon.id)}
          >
            <IconComponent class="h-4 w-4" />
          </button>
        {/each}
      </div>
    </div>
  </Popover.Content>
</Popover.Root>
