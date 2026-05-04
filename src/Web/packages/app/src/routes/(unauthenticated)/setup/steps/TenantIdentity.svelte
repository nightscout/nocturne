<script lang="ts">
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Button } from "$lib/components/ui/button";
  import { Check, Loader2, AlertTriangle, ArrowRight } from "lucide-svelte";
  import { setupTenant, validateSetupSlug, setSetupTenantSlug } from "../setup.remote";

  let {
    onComplete,
  }: {
    onComplete: (slug: string) => void;
  } = $props();

  let slug = $state("");
  let displayName = $state("");
  let slugError = $state<string | null>(null);
  let slugValid = $state(false);
  let validating = $state(false);
  let submitting = $state(false);
  let submitError = $state<string | null>(null);

  // Debounced slug validation
  let validationTimeout: ReturnType<typeof setTimeout> | null = null;

  function onSlugInput() {
    slugError = null;
    slugValid = false;

    if (validationTimeout) clearTimeout(validationTimeout);

    const value = slug.trim().toLowerCase();
    if (!value || value.length < 3) {
      if (value.length > 0) slugError = "Slug must be at least 3 characters";
      return;
    }

    validating = true;
    validationTimeout = setTimeout(async () => {
      try {
        const result = await validateSetupSlug({ slug: value });
        if (result?.isValid) {
          slugValid = true;
          slugError = null;
        } else {
          slugValid = false;
          slugError = result?.message ?? "Invalid slug";
        }
      } catch {
        slugError = "Could not validate slug";
      } finally {
        validating = false;
      }
    }, 400);
  }

  async function handleSubmit() {
    if (!slugValid || !displayName.trim()) return;
    submitting = true;
    submitError = null;

    try {
      const normalizedSlug = slug.trim().toLowerCase();
      await setupTenant({
        slug: normalizedSlug,
        displayName: displayName.trim(),
      });
      await setSetupTenantSlug(normalizedSlug);
      onComplete(normalizedSlug);
    } catch (err) {
      submitError =
        err instanceof Error ? err.message : "Failed to create tenant.";
    } finally {
      submitting = false;
    }
  }

  const canSubmit = $derived(
    slugValid && displayName.trim().length > 0 && !submitting
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
  <div class="w-full max-w-md space-y-6">
    {#if submitError}
      <div
        class="flex items-start gap-3 rounded-lg border border-red-500/20 bg-red-500/5 p-4"
      >
        <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-red-400" />
        <p class="text-sm text-red-400">{submitError}</p>
      </div>
    {/if}

    <div class="space-y-2">
      <Label for="setup-slug" class="text-white/70">Slug</Label>
      <Input
        id="setup-slug"
        bind:value={slug}
        oninput={onSlugInput}
        placeholder="my-instance"
        class="font-mono bg-white/5 border-white/10 text-white placeholder:text-white/25 {slugError
          ? 'border-red-500/50'
          : slugValid
            ? 'border-green-500/50'
            : ''}"
      />
      {#if validating}
        <p class="text-xs text-white/40">Checking availability...</p>
      {:else if slugError}
        <p class="text-xs text-red-400">{slugError}</p>
      {:else if slugValid}
        <p class="flex items-center gap-1.5 text-xs text-green-400">
          <Check class="h-3 w-3" />
          Available
        </p>
      {:else}
        <p class="text-xs text-white/30">
          Lowercase letters, numbers, and hyphens. At least 3 characters.
        </p>
      {/if}
    </div>

    <div class="space-y-2">
      <Label for="setup-display-name" class="text-white/70">
        Instance name
      </Label>
      <Input
        id="setup-display-name"
        bind:value={displayName}
        placeholder="My Nocturne"
        class="bg-white/5 border-white/10 text-white placeholder:text-white/25"
      />
      <p class="text-xs text-white/30">
        A friendly name shown in the UI. You can change this anytime.
      </p>
    </div>

    <Button class="w-full" onclick={handleSubmit} disabled={!canSubmit}>
      {#if submitting}
        <Loader2 class="mr-2 h-4 w-4 animate-spin" />
        Creating instance...
      {:else}
        Continue
        <ArrowRight class="ml-2 h-4 w-4" />
      {/if}
    </Button>
  </div>
</div>
