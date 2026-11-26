import type { SlashCommandEvent } from "chat";
import { platformModule } from "../platforms/registry.js";

/** `candidate` when it is a well-formed unique handle on `platform`, else null; see `PlatformModule.handleFormat`. */
export function platformHandle(platform: string, candidate: unknown): string | null {
  const format = platformModule(platform)?.handleFormat;
  return format && typeof candidate === "string" && format.test(candidate) ? candidate : null;
}

/** The invoking account's unique handle. */
export function slashCommandHandle(event: SlashCommandEvent): string | null {
  const platform = platformModule(event.adapter.name);
  const candidate = platform?.slashCommandHandle ? platform.slashCommandHandle(event) : event.user.userName;
  return platformHandle(event.adapter.name, candidate);
}
