<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import * as Popover from "$lib/components/ui/popover";
  import * as ToggleGroup from "$lib/components/ui/toggle-group";
  import { PROFILE_ICONS } from "$lib/constants/profile-icons";
  import type { Icon } from "@lucide/svelte";
  import User from "@lucide/svelte/icons/user";
  import UserCircle from "@lucide/svelte/icons/circle-user";
  import Heart from "@lucide/svelte/icons/heart";
  import HeartPulse from "@lucide/svelte/icons/heart-pulse";
  import Activity from "@lucide/svelte/icons/activity";
  import Syringe from "@lucide/svelte/icons/syringe";
  import Pill from "@lucide/svelte/icons/pill";
  import Droplet from "@lucide/svelte/icons/droplet";
  import Target from "@lucide/svelte/icons/target";
  import Sun from "@lucide/svelte/icons/sun";
  import Moon from "@lucide/svelte/icons/moon";
  import Sunrise from "@lucide/svelte/icons/sunrise";
  import Sunset from "@lucide/svelte/icons/sunset";
  import Dumbbell from "@lucide/svelte/icons/dumbbell";
  import Bike from "@lucide/svelte/icons/bike";
  import Footprints from "@lucide/svelte/icons/footprints";
  import Utensils from "@lucide/svelte/icons/utensils";
  import Coffee from "@lucide/svelte/icons/coffee";
  import Cake from "@lucide/svelte/icons/cake";
  import Baby from "@lucide/svelte/icons/baby";
  import Briefcase from "@lucide/svelte/icons/briefcase";
  import Home from "@lucide/svelte/icons/house";
  import Plane from "@lucide/svelte/icons/plane";
  import Zap from "@lucide/svelte/icons/zap";
  import Shield from "@lucide/svelte/icons/shield";
  import Star from "@lucide/svelte/icons/star";
  import Sparkles from "@lucide/svelte/icons/sparkles";
  import Clock from "@lucide/svelte/icons/clock";
  import Calendar from "@lucide/svelte/icons/calendar";
  import TrendingUp from "@lucide/svelte/icons/trending-up";

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
  <Popover.Trigger {disabled}>
    {#snippet child({ props }: { props: Record<string, unknown> })}
      <Button {...props} variant="outline" class="w-full justify-start">
        <CurrentIcon class="h-4 w-4" />
        <span>{currentIconName}</span>
      </Button>
    {/snippet}
  </Popover.Trigger>
  <Popover.Content class="w-80 p-3" align="start">
    <div class="space-y-2">
      <p class="text-sm font-medium">Select an icon</p>
      <ToggleGroup.Root
        type="single"
        variant="outline"
        spacing={1}
        class="grid grid-cols-6"
        value={selectedIcon}
        onValueChange={(v: string) => v && selectIcon(v)}
      >
        {#each PROFILE_ICONS as icon (icon.id)}
          {@const IconComponent = iconComponents[icon.id] ?? User}
          <ToggleGroup.Item
            value={icon.id}
            class="w-9"
            title={icon.name}
            aria-label={icon.name}
          >
            <IconComponent class="h-4 w-4" />
          </ToggleGroup.Item>
        {/each}
      </ToggleGroup.Root>
    </div>
  </Popover.Content>
</Popover.Root>
