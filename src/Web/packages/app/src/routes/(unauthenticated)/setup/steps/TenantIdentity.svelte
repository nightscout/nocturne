<script lang="ts">
  import { Input } from "$lib/components/ui/input";
  import { Button } from "$lib/components/ui/button";
  import { Check, Loader2, ArrowRight } from "lucide-svelte";
  import { FormError, FormField, useAvailability } from "$lib/forms";
  import { setupTenant, validateSetupSlug, setSetupTenantSlug } from "../setup.remote";

  let {
    onComplete,
  }: {
    onComplete: (slug: string) => void;
  } = $props();

  let slug = $state("");
  let displayName = $state("");
  let submitting = $state(false);
  let submitError = $state<string | null>(null);

  const normalizedSlug = $derived(slug.trim().toLowerCase());

  const availability = useAvailability(
    () => normalizedSlug,
    (value) => validateSetupSlug({ slug: value }),
    { label: "Slug" },
  );

  /**
   * Creates the instance. The wizard advances through client state, so this
   * step needs JavaScript; the form is here for Enter-to-submit and the
   * browser's own required-field checks.
   */
  async function handleSubmit(event: SubmitEvent) {
    event.preventDefault();
    if (!canSubmit) return;
    submitting = true;
    submitError = null;

    try {
      await setupTenant({
        slug: normalizedSlug,
        displayName: displayName.trim(),
      });
      await setSetupTenantSlug(normalizedSlug);
      onComplete(normalizedSlug);
    } catch (err) {
      console.error("Creating the instance failed:", err);
      submitError =
        "We couldn't create your instance. Please try again in a moment.";
    } finally {
      submitting = false;
    }
  }

  const canSubmit = $derived(
    availability.submittable && displayName.trim().length > 0 && !submitting
  );
</script>

<div class="flex flex-col items-center gap-10 px-4 py-8">
  <!-- Heading -->
  <div class="flex flex-col items-center gap-4 text-center">
    <h1
      class="font-[Montserrat] font-[250] leading-tight tracking-tight text-white"
      style="font-size: clamp(32px, 4vw, 48px);"
    >
      Name your <em
        class="not-italic font-light"
        style="color: var(--onb-teal);"
      >
        instance
      </em>
      .
    </h1>
    <p class="max-w-140 text-base leading-relaxed text-white/50">
      Choose a slug and display name for your Nocturne instance. The slug is a
      short, URL-friendly identifier that cannot be changed later.
    </p>
  </div>

  <!-- Form -->
  <form class="w-full max-w-md space-y-6" onsubmit={handleSubmit}>
    <FormError issues={submitError} focusOnShow />

    <FormField
      label="Slug"
      id="setup-slug"
      required
      labelClass="text-white/70"
      issues={availability.error}
    >
      {#snippet control(field)}
        <Input
          {...field}
          name="slug"
          bind:value={slug}
          placeholder="my-instance"
          autocomplete="off"
          autocapitalize="none"
          spellcheck={false}
          autofocus
          minlength={3}
          class="font-mono bg-white/5 border-white/10 text-white placeholder:text-white/25 {availability.error
            ? 'border-red-500/50'
            : availability.valid
              ? 'border-green-500/50'
              : ''}"
        />
      {/snippet}
      {#snippet hint()}
        {#if availability.validating}
          <p class="text-xs text-white/40">Checking availability...</p>
        {:else if availability.valid}
          <p class="flex items-center gap-1.5 text-xs text-green-400">
            <Check class="h-3 w-3" />
            Available
          </p>
        {:else}
          <p class="text-xs text-white/30">
            Lowercase letters, numbers, and hyphens. At least 3 characters.
          </p>
        {/if}
      {/snippet}
    </FormField>

    <FormField
      label="Instance name"
      id="setup-display-name"
      required
      labelClass="text-white/70"
      description="A friendly name shown in the UI. You can change this anytime."
    >
      {#snippet control(field)}
        <Input
          {...field}
          name="displayName"
          bind:value={displayName}
          placeholder="My Nocturne"
          autocomplete="organization"
          class="bg-white/5 border-white/10 text-white placeholder:text-white/25"
        />
      {/snippet}
    </FormField>

    <Button type="submit" class="w-full" disabled={!canSubmit}>
      {#if submitting}
        <Loader2 class="mr-2 h-4 w-4 animate-spin" />
        Creating instance...
      {:else}
        Continue
        <ArrowRight class="ml-2 h-4 w-4" />
      {/if}
    </Button>
  </form>
</div>
