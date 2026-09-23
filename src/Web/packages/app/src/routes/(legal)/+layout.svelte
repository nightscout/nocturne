<script lang="ts">
  import { resolve } from "$app/paths";
  import { Button } from "$lib/components/ui/button";
  import { ArrowLeft } from "lucide-svelte";

  let { children } = $props();
</script>

<svelte:head>
  <meta name="robots" content="noindex" />
</svelte:head>

<div class="min-h-screen flex flex-col bg-background">
  <!-- Header -->
  <header class="border-b border-border/40">
    <div class="container mx-auto px-4 py-4">
      <a href={resolve("/")} class="flex items-center gap-2.5 hover:opacity-80 transition-opacity">
        <div class="w-8 h-8 rounded-lg bg-primary/20 flex items-center justify-center">
          <span class="text-primary font-bold">N</span>
        </div>
        <span class="text-lg font-semibold">Nocturne</span>
      </a>
    </div>
  </header>

  <!-- Content -->
  <main class="flex-1 container mx-auto px-4 py-8">
    <article class="max-w-3xl mx-auto prose prose-slate dark:prose-invert">
      {@render children()}
    </article>
  </main>

  <!-- Footer -->
  <footer class="border-t border-border/40 bg-muted/30">
    <div class="container mx-auto px-4 py-6 flex flex-col md:flex-row justify-between items-center gap-4">
      <Button variant="ghost" href="/auth/login">
        <ArrowLeft class="w-4 h-4" />
        Back to Login
      </Button>
      <p class="text-sm text-muted-foreground">
        Last updated: February 21, 2026
      </p>
    </div>
  </footer>
</div>
