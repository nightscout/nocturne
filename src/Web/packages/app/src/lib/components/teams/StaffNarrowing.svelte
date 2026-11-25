<script lang="ts">
  import { accessEndDate } from "#lib/components/members/access-end.js";
  import { formatMediumDate } from "#lib/utils/formatting.js";
  import type { TeamStaffNarrowing, TeamSuggestableRole } from "@nocturne/api-client";

  interface Props {
    narrowing: TeamStaffNarrowing | undefined;
    roles: TeamSuggestableRole[];
    /** Whether the end has passed, as the API says; absent where the API does not say, as for an invite. */
    hasEnded?: boolean;
  }

  let { narrowing, roles, hasEnded = false }: Props = $props();

  const ceilingSlug = $derived(narrowing?.roleCeilingSlug);
  const ceilingName = $derived(roles.find((r) => r.slug === ceilingSlug)?.name);
</script>

<span>
  {#if !ceilingSlug}
    Everything the team has
  {:else if ceilingName}
    No more than the role {ceilingName}
  {:else}
    No more than a role that is no longer offered
  {/if}
  {#if narrowing?.limitTo24Hours}
    · Only the last 24 hours
  {/if}
  {#if narrowing?.endsAt && hasEnded}
    · Access ended {formatMediumDate(accessEndDate(narrowing.endsAt))}
  {:else if narrowing?.endsAt}
    · Access ends {formatMediumDate(accessEndDate(narrowing.endsAt))}
  {/if}
</span>
