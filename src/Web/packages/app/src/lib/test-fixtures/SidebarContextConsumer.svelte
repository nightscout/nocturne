<script lang="ts">
  import { useSidebar } from "$lib/components/ui/sidebar/context.svelte.js";

  // Dereferenced on purpose: useSidebar() answers undefined rather than
  // throwing, so a provider whose setContext never ran stays invisible until
  // a descendant reads through it — which is how this reached production.
  const sidebar = useSidebar();
</script>

<!-- String(), not a literal: a literal here would be a message of its own,
     and this suite loads no catalog. -->
<span data-testid="probe">{String(sidebar.isMobile)}</span>
