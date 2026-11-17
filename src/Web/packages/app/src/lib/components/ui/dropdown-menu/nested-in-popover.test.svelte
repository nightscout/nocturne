<script lang="ts">
  import * as Popover from "#lib/components/ui/popover/index.js";
  import * as DropdownMenu from "#lib/components/ui/dropdown-menu/index.js";

  interface Props {
    onselect: () => void;
  }

  let { onselect }: Props = $props();
</script>

<Popover.Root>
  <Popover.Trigger>Notifications</Popover.Trigger>
  <Popover.Content>
    <p>Popover body</p>
    <DropdownMenu.Root>
      <DropdownMenu.Trigger>Do not disturb</DropdownMenu.Trigger>
      <DropdownMenu.Content>
        <DropdownMenu.Item onSelect={onselect}>30 minutes</DropdownMenu.Item>
      </DropdownMenu.Content>
    </DropdownMenu.Root>
  </Popover.Content>
</Popover.Root>
