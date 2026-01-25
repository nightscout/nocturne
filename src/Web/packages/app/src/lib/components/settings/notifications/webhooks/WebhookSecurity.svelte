<script lang="ts">
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";

  interface Props {
    secret: string;
    hasSecret: boolean;
    disabled?: boolean;
  }

  let {
    secret,
    hasSecret,
    disabled = false,
  }: Props = $props();
</script>

<div class="space-y-2">
  <Label for="webhook-secret">Signature Secret</Label>
  <Input
    id="webhook-secret"
    type="text"
    readonly
    placeholder={hasSecret ? "Secret already configured" : "Secret will be generated on save"}
    value={secret}
    disabled={disabled}
  />
  <p class="text-xs text-muted-foreground">
    The API signs each webhook with HMAC-SHA256 using this secret.
  </p>
</div>
