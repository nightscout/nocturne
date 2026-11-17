import { relativeAge } from "@nocturne/ui/relative-time";

export function issueAge(updatedAt: string, locale: string): string {
  return relativeAge(updatedAt, locale);
}
