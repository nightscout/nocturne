<script lang="ts">
  import { resourcesOf } from '../spec';
  import SpecBlock from './SpecBlock.svelte';

  let { group }: { group: string } = $props();
</script>

<SpecBlock rows={(spec, document) => resourcesOf(spec, document, group)}>
  {#snippet head()}
    <th>Resource</th>
    <th>What it is</th>
    <th class="count">Operations</th>
  {/snippet}
  {#snippet row(resource)}
    <td><a href={resource.href}>{resource.label}</a></td>
    <td>{resource.summary}</td>
    <td class="count">{resource.operations}</td>
  {/snippet}
</SpecBlock>
