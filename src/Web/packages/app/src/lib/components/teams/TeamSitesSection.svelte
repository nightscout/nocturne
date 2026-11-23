<script lang="ts">
  import * as Card from "#lib/components/ui/card/index.js";
  import { accessEndDate } from "#lib/components/members/access-end.js";
  import { formatMediumDate, formatMediumDateTime } from "#lib/utils/formatting.js";
  import type { TeamSiteAccess } from "@nocturne/api-client";

  interface Props {
    sites: TeamSiteAccess[];
  }

  let { sites }: Props = $props();
</script>

<Card.Root>
  <Card.Header>
    <Card.Title>Sites this team has access to</Card.Title>
  </Card.Header>
  <Card.Content>
    {#if sites.length === 0}
      <p class="text-sm text-muted-foreground">No site has given this team access.</p>
    {:else}
      <ul class="divide-y">
        {#each sites as site (site.tenantId)}
          <li class="space-y-1 py-3">
            <p class="font-medium">{site.displayName}</p>
            <p class="text-sm text-muted-foreground">Roles: {(site.roleNames ?? []).join(", ")}</p>
            <p class="text-sm text-muted-foreground">
              {site.limitTo24Hours ? "Only the last 24 hours" : "All history"}
              ·
              {#if site.accessEndsAt && site.hasEnded}
                Access ended {formatMediumDate(accessEndDate(site.accessEndsAt))}
              {:else if site.accessEndsAt}
                Access ends {formatMediumDate(accessEndDate(site.accessEndsAt))}
              {:else}
                Access does not end
              {/if}
              · Given {formatMediumDateTime(site.givenAt)}
            </p>
          </li>
        {/each}
      </ul>
    {/if}
  </Card.Content>
</Card.Root>
