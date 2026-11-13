<!--
  Links between the pages of a report made of several (a report category with `pages`), for the
  report header: the pages the viewer can load, even on a page it cannot. Each keeps the query
  string, so the pages share their range and filters. The custom report pages link to the list, to
  each saved report and to a new report.
-->
<script lang="ts">
  import { page } from "$app/state";
  import { getCustomReports } from "#lib/api/generated/customReports.generated.remote.js";
  import { reportCategories, reportPagePath, visibleReportCategories } from "#lib/navigation/report-navigation.svelte.js";
  import { cn } from "#lib/utils/index.js";

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
  const customPages = $derived(page.url.pathname === "/reports/custom" || page.url.pathname.startsWith("/reports/custom/"));
  const saved = $derived(customPages ? getCustomReports() : null);
  const linkClass =
    "rounded-md px-2.5 py-1 text-sm pointer-coarse:py-2 font-medium whitespace-nowrap text-muted-foreground transition-colors hover:bg-muted hover:text-foreground";
</script>

{#snippet customLink(path: string, label: string)}
  {@const current = path === page.url.pathname}
  <!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- reportPagePath puts the app base before the path -->
  <a href={reportPagePath(path)} aria-current={current ? "page" : undefined} class={cn(linkClass, current && "bg-muted text-foreground")}>
    {label}
  </a>
{/snippet}

{#if customPages}
  <div class="relative border-t border-border px-3 @md:px-6 print:hidden">
    <nav aria-label="Custom reports" class="flex flex-wrap gap-1 py-1.5" data-testid="report-pages">
      {@render customLink("/reports/custom", "All custom reports")}
      {#each saved?.current ?? [] as report (report.id)}
        {@render customLink(`/reports/custom/${report.id}`, report.name ?? "")}
      {/each}
      {@render customLink("/reports/custom/new", "New")}
    </nav>
  </div>
{/if}

{#if family && reachable.length > 0}
  <div class="relative border-t border-border px-3 @md:px-6 print:hidden">
    <nav aria-label="{family.title} pages" class="flex flex-wrap gap-1 py-1.5" data-testid="report-pages">
      {#each reachable as report (report.href)}
        {@const current = report.href === page.url.pathname}
        <!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- reportPagePath puts the app base before the path -->
        <a href={reportPagePath(report.href, page.url.search)}
          aria-current={current ? "page" : undefined}
          class={cn(
            "rounded-md px-2.5 py-1 text-sm pointer-coarse:py-2 font-medium whitespace-nowrap text-muted-foreground transition-colors hover:bg-muted hover:text-foreground",
            current && "bg-muted text-foreground"
          )}
        >
          {report.sidebarTitle ?? report.title}
        </a>
      {/each}
    </nav>
  </div>
{/if}
