<script lang="ts">
  import FlaskConical from "@lucide/svelte/icons/flask-conical";
  import { Banner } from "#lib/components/ui/banner/index.js";
  import type { DemoPersona } from "#lib/authorization/demo-subject.js";

  interface Props {
    /** The persona the visitor is signed in as. */
    persona: DemoPersona;
    /**
     * When the demo's data next resets, which is the countdown of the personal
     * persona.
     */
    nextResetAt: string | null;
    /**
     * When the clinic persona's views and rules next reset, which is the
     * countdown of the clinic persona.
     */
    nextVisitorResetAt: string | null;
    /**
     * Whether this site can sign the visitor in as the other persona: only the
     * demo host can.
     */
    canSwitch?: boolean;
  }

  const {
    persona,
    nextResetAt,
    nextVisitorResetAt,
    canSwitch = true,
  }: Props = $props();

  const SESSION_ENDPOINT = "/api/v4/demo/session";

  const clinic = $derived(persona === "clinic");
  const resetAt = $derived(clinic ? nextVisitorResetAt : nextResetAt);
  const other = $derived(
    clinic
      ? { label: "See it as a person", href: `${SESSION_ENDPOINT}?redirect=/` }
      : {
          label: "See it as a clinic",
          href: `${SESSION_ENDPOINT}?as=clinic&redirect=/people`,
        }
  );

  let now = $state(Date.now());

  $effect(() => {
    const interval = setInterval(() => {
      now = Date.now();
    }, 60_000);
    return () => clearInterval(interval);
  });

  const remaining = $derived.by(() => {
    if (!resetAt) return null;
    const ms = new Date(resetAt).getTime() - now;
    if (ms <= 0) return "Resetting soon";
    const totalMinutes = Math.floor(ms / 60_000);
    const hours = Math.floor(totalMinutes / 60);
    const minutes = totalMinutes % 60;
    if (hours > 0) return `Resets in ${hours}h ${minutes}m`;
    return `Resets in ${minutes}m`;
  });
</script>

<Banner variant="info">
  <div class="flex items-center gap-2">
    <FlaskConical class="h-4 w-4 shrink-0" />
    <span data-testid="demo-banner-persona">
      {clinic
        ? "Demo, Demotown Diabetes Clinic, synthetic data"
        : "Demo, synthetic data"}
    </span>
  </div>
  <div class="flex shrink-0 items-center gap-3">
    {#if remaining}
      <span class="font-medium">{remaining}</span>
    {/if}
    {#if canSwitch}
      <a
        href={other.href}
        rel="external"
        class="font-medium underline underline-offset-2 hover:no-underline"
        data-testid="demo-banner-switch"
      >
        {other.label}
      </a>
    {/if}
  </div>
</Banner>
