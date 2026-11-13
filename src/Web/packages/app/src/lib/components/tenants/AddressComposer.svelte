<script lang="ts">
  import Check from "@lucide/svelte/icons/check";
  import CircleX from "@lucide/svelte/icons/circle-x";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import { Input } from "#lib/components/ui/input/index.js";
  import type { SlugAvailability } from "./slug-availability.svelte.js";

  interface Props {
    /** The subdomain as typed. */
    value: string;
    /** The domain every site's subdomain sits under, for example nocturne.example. */
    baseDomain: string;
    availability: SlugAvailability;
    id?: string;
  }

  let { value = $bindable(), baseDomain, availability, id = "site-subdomain" }: Props = $props();

  const statusId = $derived(`${id}-status`);
</script>

<!--
  The subdomain is typed into the address itself, so the reader sees the whole address they will
  use rather than a field and a suffix. The hidden twin of the input sizes it to its text.
-->
<div class="space-y-2">
  <label
    for={id}
    class="flex cursor-text flex-wrap items-baseline justify-center rounded-2xl border bg-background px-3 pt-5 pb-4 font-brand text-xl font-light tracking-tight shadow-xs transition-shadow focus-within:border-ring focus-within:ring-3 focus-within:ring-ring/50 @lg:text-3xl @lg:px-5"
  >
    <span class="hidden text-muted-foreground @lg:inline">https://</span>
    <span class="address-grow inline-grid" data-value={value || "name"}>
      <Input
        {id}
        bind:value
        variant="inline"
        autocomplete="off"
        autocapitalize="off"
        spellcheck={false}
        inputmode="url"
        placeholder="name"
        aria-describedby={statusId}
        aria-invalid={availability.state === "refused"}
        class="col-start-1 row-start-1 w-0 min-w-full"
      />
    </span>
    <span class="text-muted-foreground">.{baseDomain}</span>
  </label>

  <p id={statusId} class="flex min-h-5 items-center justify-center gap-1.5 text-sm" aria-live="polite">
    {#if availability.state === "checking"}
      <Loader2 class="h-3.5 w-3.5 animate-spin text-muted-foreground" />
      <span class="text-muted-foreground">Checking the address...</span>
    {:else if availability.state === "available"}
      <Check class="h-3.5 w-3.5 text-success" />
      <span class="text-success">This address is available.</span>
    {:else if availability.state === "refused"}
      <CircleX class="h-3.5 w-3.5 text-destructive" />
      <span class="text-destructive">{availability.message}</span>
    {:else}
      <span class="text-muted-foreground">Use 3 to 63 lowercase letters, numbers, and hyphens.</span>
    {/if}
  </p>

  <p class="text-center text-sm text-muted-foreground">
    Not <span class="font-mono text-xs">{baseDomain}</span>. That is the address of the server, and many sites share it.
  </p>
</div>

<style>
  .address-grow::after {
    content: attr(data-value) " ";
    grid-area: 1 / 1;
    visibility: hidden;
    white-space: pre;
    font-weight: 500;
    padding-inline: 2px;
  }
</style>
