/**
 * The bot's state database. The bot adapter expects a postgresql:// URL, not the .NET-style
 * ConnectionStrings__nocturne-postgres value (which is Host=...;Port=...;Database=... key/value
 * format and causes the pg client to resolve literal strings like "base" as hostnames). Aspire
 * injects NOCTURNE_POSTGRES_URI pointing at the nocturne_web role — a least-privileged role that
 * owns only its own chat_state_* tables and cannot touch tenant-scoped tables. Fall back to
 * DATABASE_URL for non-Aspire deployments.
 */
export function botPostgresUrl(): string {
	return process.env.NOCTURNE_POSTGRES_URI ?? process.env.DATABASE_URL ?? "";
}
