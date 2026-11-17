#!/bin/sh
# Compose NOCTURNE_POSTGRES_URI from the nocturne_web role pieces the chart sets;
# honor a directly-provided URI (Aspire/compose) and forward the CMD via "$@".
if [ -n "${WEB_DB_PASSWORD:-}" ]; then
	export NOCTURNE_POSTGRES_URI="postgresql://nocturne_web:${WEB_DB_PASSWORD}@${WEB_DB_HOST}:${WEB_DB_PORT}/${WEB_DB_NAME}"
fi
exec "$@"
