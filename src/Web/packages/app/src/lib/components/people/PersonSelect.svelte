<script lang="ts">
  import type { Snippet } from "svelte";
  import type { TenantOverviewItem } from "@nocturne/api-client";
  import { getPersonSelection } from "./person-selection.js";

  interface Props {
    person: TenantOverviewItem;
    class?: string;
    children: Snippet;
  }

  const { person, class: className = "", children }: Props = $props();

  const selection = getPersonSelection();
  const selected = $derived(
    person.slug != null && selection.selectedSlug === person.slug
  );

  // A modified click or a middle click opens the same page in a new tab, which opens the person there.
  function onclick(event: MouseEvent) {
    if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
      return;
    }
    event.preventDefault();
    selection.select(person);
  }
</script>

<!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- the current page's URL with the person set, already resolved -->
<a href={selection.hrefFor(person)}
  aria-current={selected ? "true" : undefined}
  data-testid="person-row"
  data-person={person.slug}
  class="block outline-none transition-colors hover:bg-accent/50 focus-visible:ring-3 focus-visible:ring-inset focus-visible:ring-ring/50 aria-[current=true]:bg-accent {className}"
  {onclick}
>
  {@render children()}
</a>
