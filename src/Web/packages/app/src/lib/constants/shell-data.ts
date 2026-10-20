/** The `depends` key of the loads behind the authenticated shell: session, grants, clamp, guest link. */
export const SHELL_DATA_KEY = "app:shell";

/** Coarse on purpose: the shell load costs a session check and a status probe, and grants rarely change. */
export const SHELL_REVALIDATE_INTERVAL_MS = 5 * 60 * 1000;
