<script lang="ts" module>
  /** How long the search waits after the last key before the list is read for it. */
  export const SEARCH_DEBOUNCE_MS = 300;
</script>

<script lang="ts">
  import { untrack } from "svelte";
  import { Debounced } from "runed";
  import * as InputGroup from "#lib/components/ui/input-group/index.js";
  import Search from "@lucide/svelte/icons/search";
  import { MAX_SEARCH_LENGTH } from "./people-query.js";

  interface Props {
    /** The search the list was read for. */
    value: string;
    onSearch: (text: string) => void;
  }

  const { value, onSearch }: Props = $props();

  // svelte-ignore state_referenced_locally
  let text = $state(value);
  // svelte-ignore state_referenced_locally
  let sent = value;
  const debounced = new Debounced(() => text, SEARCH_DEBOUNCE_MS);

  // A search set from outside, as by a change of tab, replaces what the box holds.
  $effect(() => {
    if (value === sent) return;
    sent = value;
    text = value;
  });

  $effect(() => {
    const settled = debounced.current;
    if (settled === sent) return;
    sent = settled;
    untrack(() => onSearch(settled));
  });
</script>

<InputGroup.Root class="w-full @2xl:w-64">
  <InputGroup.Addon>
    <Search aria-hidden="true" />
  </InputGroup.Addon>
  <InputGroup.Input
    type="search"
    placeholder="Search by name"
    aria-label="Search by name"
    maxlength={MAX_SEARCH_LENGTH}
    bind:value={text}
    data-testid="people-search"
  />
</InputGroup.Root>
