<script lang="ts">
  import '../app.css';

  let { children } = $props();
</script>

<div class="min-h-screen bg-gradient-to-br from-background via-background to-primary/5">
  <header class="border-b border-border/40 backdrop-blur-sm">
    <div class="container mx-auto px-4 py-4 flex items-center gap-4">
      <div class="flex items-center gap-2">
        <div class="w-8 h-8 rounded-lg bg-primary/20 flex items-center justify-center">
          <span class="text-primary font-bold">N</span>
        </div>
        <span class="text-xl font-semibold">Nocturne Portal</span>
      </div>
    </div>
  </header>

  <main class="container mx-auto px-4 py-8">
    {@render children()}
  </main>

  <footer class="border-t border-border/40 mt-auto">
    <div class="container mx-auto px-4 py-6 text-center text-muted-foreground text-sm">
      Nocturne &copy; 2026 - Open source diabetes management
    </div>
  </footer>
</div>
