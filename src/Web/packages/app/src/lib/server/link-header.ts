const MODULEPRELOAD = /;\s*rel="?modulepreload"?\s*(?:;|$)/i;

export function dropModulepreloads(link: string): string {
  return link
    .split(/,\s*(?=<)/)
    .filter((entry) => !MODULEPRELOAD.test(entry))
    .join(", ");
}
