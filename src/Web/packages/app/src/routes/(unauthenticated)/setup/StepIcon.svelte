<script lang="ts" module>
  import Moon from "@lucide/svelte/icons/moon";
  import Fingerprint from "@lucide/svelte/icons/fingerprint";
  import Users from "@lucide/svelte/icons/users";
  import Globe from "@lucide/svelte/icons/globe";
  import Plug from "@lucide/svelte/icons/plug";
  import Database from "@lucide/svelte/icons/database";
  import CircleCheck from "@lucide/svelte/icons/circle-check";

  export const STEP_ICONS = {
    welcome: Moon,
    account: Fingerprint,
    who: Users,
    units: Globe,
    source: Plug,
    import: Database,
    done: CircleCheck,
  } as const;

  export type StepArt = keyof typeof STEP_ICONS;
</script>

<script lang="ts">
  let { art, class: className }: { art: StepArt; class?: string } = $props();

  const Icon = $derived(STEP_ICONS[art]);
</script>

<Icon data-testid="step-icon" data-art={art} aria-hidden="true" class={className} />
