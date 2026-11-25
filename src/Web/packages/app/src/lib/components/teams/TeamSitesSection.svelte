<script lang="ts">
  import * as Card from "#lib/components/ui/card/index.js";
  import { accessEndDate } from "#lib/components/members/access-end.js";
  import { formatMediumDate, formatMediumDateTime } from "#lib/utils/formatting.js";
  import { plural } from "#lib/utils/plural.js";
  import type { TeamSiteAccess } from "@nocturne/api-client";

  interface Props {
    sites: TeamSiteAccess[];
  }

  let { sites }: Props = $props();
</script>

<Card.Root>
  <Card.Header>
    <Card.Title>Sites this team has access to</Card.Title>
    <Card.Description>
      Each site decides if the team or the site approves the team's staff. Only an owner of the site can
      change this.
    </Card.Description>
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
            {#if !site.hasEnded}
              <p class="text-sm text-muted-foreground">
                {#if site.teamManagesStaff && (site.declinedStaffCount ?? 0) > 0}
                  The team decides which of its staff can see this site. Staff that the site declined stay declined.
                {:else if site.teamManagesStaff}
                  The team decides which of its staff can see this site.
                {:else}
                  An owner of the site must approve each staff member.
                {/if}
              </p>
              {#if (site.pendingStaffCount ?? 0) > 0}
                <p class="text-sm text-muted-foreground">
                  {plural(site.pendingStaffCount ?? 0, [
                    "# staff member waits for the site's approval.",
                    "# staff members wait for the site's approval.",
                  ])}
                </p>
              {/if}
              {#if (site.declinedStaffCount ?? 0) > 0}
                <p class="text-sm text-muted-foreground">
                  {plural(site.declinedStaffCount ?? 0, [
                    "The site declined # staff member.",
                    "The site declined # staff members.",
                  ])}
                </p>
              {/if}
            {/if}
          </li>
        {/each}
      </ul>
    {/if}
  </Card.Content>
</Card.Root>
