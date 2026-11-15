<!--
  Links between the pages of a report made of several (a report category with `pages`), for the
  report header: the pages the viewer can load, even on a page it cannot. Each keeps the query
  string, so the pages share their range and filters. The custom report pages show where the viewer
  is (the list, a saved report, its editor or a new report) and a menu of the other saved reports.
-->
<script lang="ts">
  import { page } from "$app/state";
  import { getCustomReports } from "#lib/api/generated/customReports.generated.remote.js";
  import { reportCategories, reportPagePath, visibleReportCategories } from "#lib/navigation/report-navigation.svelte.js";
  import { cn } from "#lib/utils/index.js";
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as DropdownMenu from "#lib/components/ui/dropdown-menu/index.js";
  import ChevronDown from "@lucide/svelte/icons/chevron-down";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import Plus from "@lucide/svelte/icons/plus";

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

{#if customPages}
  {@const currentId = /^\/reports\/custom\/([^/]+)/.exec(page.url.pathname)?.[1]}
  {@const current = currentId === "new" ? null : (saved?.current ?? []).find((r) => r.id === currentId)}
  {@const editing = page.url.pathname.endsWith("/edit")}
  <div class="relative border-t border-border px-3 @md:px-6 print:hidden">
    <nav aria-label="Custom reports" class="flex min-h-9 items-center gap-1 py-1 text-sm" data-testid="report-pages">
      {#if page.url.pathname === "/reports/custom"}
        <span class="px-2.5 font-medium text-foreground" aria-current="page">All custom reports</span>
      {:else}
        <!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- reportPagePath puts the app base before the path -->
        <a href={reportPagePath("/reports/custom")} class={linkClass}>All custom reports</a>
        <ChevronRight class="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
        {#if currentId === "new"}
          <span class="truncate px-1 font-medium text-foreground" aria-current="page">New report</span>
        {:else if current && editing}
          <!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- reportPagePath puts the app base before the path -->
          <a href={reportPagePath(`/reports/custom/${current.id}`)} class={cn(linkClass, "min-w-0 truncate")}>{current.name}</a>
          <ChevronRight class="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
          <span class="px-1 font-medium text-foreground" aria-current="page">Edit</span>
        {:else if current}
          <span class="min-w-0 truncate px-1 font-medium text-foreground" aria-current="page">{current.name}</span>
        {/if}
      {/if}
      <DropdownMenu.Root>
        <DropdownMenu.Trigger>
          {#snippet child({ props }: { props: Record<string, unknown> })}
            <Button {...props} variant="ghost-muted" size="xs" class="ml-auto shrink-0" data-testid="report-switcher">
              Other reports
              <ChevronDown aria-hidden="true" />
            </Button>
          {/snippet}
        </DropdownMenu.Trigger>
        <DropdownMenu.Content align="end" class="max-h-80 overflow-y-auto">
          {#each saved?.current ?? [] as report (report.id)}
            <DropdownMenu.Item onSelect={() => goto(resolve("/(authenticated)/reports/custom/[id]", { id: report.id ?? "" }))}>
              {report.name}
            </DropdownMenu.Item>
          {/each}
          <DropdownMenu.Separator />
          <DropdownMenu.Item onSelect={() => goto(resolve("/(authenticated)/reports/custom/new"))}>
            <Plus aria-hidden="true" />
            New report
          </DropdownMenu.Item>
        </DropdownMenu.Content>
      </DropdownMenu.Root>
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
