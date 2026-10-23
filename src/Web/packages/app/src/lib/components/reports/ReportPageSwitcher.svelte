<!--
  Links between the pages of a report made of several (a report category with `pages`), for the
  report header: the pages the viewer can load, even on a page it cannot. Each keeps the query
  string, so the pages share their range and filters.
-->
<script lang="ts">
  import { page } from "$app/state";
  import { reportCategories, visibleReportCategories } from "$lib/navigation/report-navigation.svelte";
  import { cn } from "$lib/utils";

  const family = $derived(
    reportCategories().find((c) => c.pages && c.reports.some((r) => r.href === page.url.pathname))
  );
  const reachable = $derived(
    family
      ? (visibleReportCategories({ grantedScopes: page.data.effectivePermissions ?? [], anonymous: !page.data.user })
          .find((c) => c.id === family.id)
          ?.reports.filter((r) => r.status === "available") ?? [])
      : []
  );
</script>

{#if family && reachable.length > 0}
  <div class="border-t border-border px-3 @md:px-6 print:hidden">
    <nav aria-label="{family.title} pages" class="flex flex-wrap gap-1 py-1.5" data-testid="report-pages">
      {#each reachable as report (report.href)}
        {@const current = report.href === page.url.pathname}
        <!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- report.href is a literal in-app path from report-navigation.svelte.ts -->
        <a href="{report.href}{page.url.search}"
          aria-current={current ? "page" : undefined}
          class={cn(
            "rounded-md px-2.5 py-1 text-sm font-medium whitespace-nowrap text-muted-foreground transition-colors hover:bg-muted hover:text-foreground",
            current && "bg-muted text-foreground"
          )}
        >
          {report.sidebarTitle ?? report.title}
        </a>
      {/each}
    </nav>
  </div>
{/if}
