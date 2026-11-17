<script module lang="ts">
  import type { ArtworkId } from "water-ui";
  import FileText from "@lucide/svelte/icons/file-text";
  import Clock from "@lucide/svelte/icons/clock";
  import Plug from "@lucide/svelte/icons/plug";
  import Timer from "@lucide/svelte/icons/timer";
  import MessageCircle from "@lucide/svelte/icons/message-circle";
  import Shield from "@lucide/svelte/icons/shield";
  import Smartphone from "@lucide/svelte/icons/smartphone";
  import Users from "@lucide/svelte/icons/users";
  import Search from "@lucide/svelte/icons/search";
  import KeyRound from "@lucide/svelte/icons/key-round";
  import CircleCheck from "@lucide/svelte/icons/circle-check";
  import Apple from "@lucide/svelte/icons/apple";
  import Globe from "@lucide/svelte/icons/globe";
  import Sunrise from "@lucide/svelte/icons/sunrise";
  import Luggage from "@lucide/svelte/icons/luggage";
  import Link from "@lucide/svelte/icons/link";
  import Moon from "@lucide/svelte/icons/moon";
  import Radio from "@lucide/svelte/icons/radio";
  import Calendar from "@lucide/svelte/icons/calendar";
  import BellRing from "@lucide/svelte/icons/bell-ring";

  /** The catalogue artworks an empty state paints, as the Lucide glyph each stands for. */
  const CATALOGUE_GLYPHS: Partial<Record<ArtworkId, typeof Clock>> = {
    "report-pages": FileText,
    clock: Clock,
    plug: Plug,
    stopwatch: Timer,
    "chat-bubble": MessageCircle,
    shield: Shield,
    phone: Smartphone,
    "people-group": Users,
    "magnifying-glass": Search,
    key: KeyRound,
    "confirmation-mark": CircleCheck,
    apple: Apple,
    "world-globe": Globe,
    sunrise: Sunrise,
    suitcase: Luggage,
    "linked-rings": Link,
    "crescent-moon": Moon,
    "cgm-sensor": Radio,
    calendar: Calendar,
    "alarm-bell": BellRing,
  };

  /** Whether an empty state's art has a plain glyph to stand in for the painting. */
  export function hasPlainGlyph(art: ArtworkId | { name: string }): boolean {
    return typeof art !== "string" || CATALOGUE_GLYPHS[art] !== undefined;
  }
</script>

<script lang="ts">
  import type { IconArtworkSource } from "water-ui";

  interface Props {
    art: ArtworkId | IconArtworkSource;
    class?: string;
  }

  let { art, class: className }: Props = $props();

  const Glyph = $derived(typeof art === "string" ? CATALOGUE_GLYPHS[art] : undefined);
</script>

<!-- The painting's stand-in where there is to be no watercolour: the glyph the artwork draws. -->
{#if typeof art !== "string"}
  <svg
    xmlns="http://www.w3.org/2000/svg"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    stroke-width="1.25"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
    data-testid="empty-state-glyph"
    class="text-muted-foreground {className}"
  >
    {#each art.icon as [tag, attrs], index (index)}
      <svelte:element this={tag} {...attrs} />
    {/each}
  </svg>
{:else if Glyph}
  <Glyph class="text-muted-foreground {className}" strokeWidth={1.25} aria-hidden="true" data-testid="empty-state-glyph" />
{/if}
