<script lang="ts">
  import type { Snippet } from "svelte";
  import { page } from "$app/state";
  import { resolve } from "$app/paths";
  import AppLogo from "$lib/components/ui/AppLogo.svelte";

  let {
    actions,
    children,
  }: {
    /** The header's right-hand side, before the signed-in user. */
    actions?: Snippet;
    children: Snippet;
  } = $props();

  const userEmail = $derived<string>(page.data?.user?.email ?? "");
  const userInitials = $derived(
    userEmail ? userEmail.slice(0, 2).toUpperCase() : "U"
  );
</script>

<div
  class="relative min-h-screen grid grid-rows-[auto_1fr_auto] bg-background text-foreground"
>
  <header
    class="relative z-50 flex items-center justify-between px-5 py-3.5 md:px-8 md:py-5.5 border-b bg-background/80 backdrop-blur-md"
  >
    <div class="flex items-center gap-3">
      <AppLogo icon="nocturne" class="h-7 w-7" />
      <span class="font-brand text-xl font-light tracking-wide">nocturne</span>
    </div>

    <div class="flex items-center gap-5">
      {@render actions?.()}

      {#if userEmail}
        <div
          class="flex items-center gap-2.5 rounded-full border bg-muted/50 py-1 pl-1 pr-3"
        >
          <span
            class="flex h-6 w-6 items-center justify-center rounded-full bg-primary text-xs font-bold text-primary-foreground"
          >
            {userInitials}
          </span>
          <span class="hidden text-xs text-muted-foreground sm:inline">
            {userEmail}
          </span>
        </div>
      {/if}
    </div>
  </header>

  <main
    class="relative px-5 py-7 pb-10 md:px-8 md:py-12 md:pb-16"
  >
    {@render children()}
  </main>

  <footer
    class="relative px-5 py-5 md:px-8 border-t flex flex-wrap gap-2.5 justify-between items-center text-xs text-muted-foreground"
  >
    <div class="flex flex-wrap items-center gap-5">
      <span>&copy; 2026 Nocturne</span>
      <a href={resolve("/privacy")} class="hover:text-foreground">Privacy</a>
      <a href="/docs" rel="external" class="hover:text-foreground">Docs</a>
    </div>
    <a
      href="https://github.com/nightscout/nocturne"
      class="inline-flex items-center gap-1.5 hover:text-foreground"
      target="_blank"
      rel="noopener noreferrer"
    >
      <AppLogo class="size-4" icon="github" />
      Source
    </a>
  </footer>
</div>
