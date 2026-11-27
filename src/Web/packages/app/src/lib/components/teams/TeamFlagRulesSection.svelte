<script lang="ts">
  import * as Card from "#lib/components/ui/card/index.js";
  import FlagRulesEditor from "#lib/components/people/FlagRulesEditor.svelte";

  interface Props {
    teamId: string;
    /** Whether the viewer manages the team. Staff see the rules and cannot change them. */
    isManager: boolean;
  }

  const { teamId, isManager }: Props = $props();
</script>

<Card.Root data-testid="team-flag-rules">
  <Card.Header>
    <Card.Title>Flag rules</Card.Title>
    <Card.Description>
      {#if isManager}
        The staff of this team see a person who meets one of these rules in Flagged, on the People page and on the wall.
      {:else}
        You see a person who meets one of these rules in Flagged, on the People page and on the wall. Only a manager can
        change the rules.
      {/if}
    </Card.Description>
  </Card.Header>
  <Card.Content>
    <FlagRulesEditor owner={{ kind: "team", teamId }} canEdit={isManager} />
  </Card.Content>
</Card.Root>
