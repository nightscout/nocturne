<script lang="ts">
  import { untrack, type Snippet } from 'svelte';
  import { seedFromName } from '../types';
  import { createDropGroup, setDropGroup } from './drop-group';

  let {
    name = '',
    children,
  }: {
    /**
     * Seeds the whole run, so the same grid paints the same way across
     * renders and two runs on one page do not mirror each other.
     */
    name?: string;
    children: Snippet;
  } = $props();

  setDropGroup(createDropGroup(seedFromName(untrack(() => name))));
</script>

{@render children()}
