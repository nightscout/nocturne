{{/* Chart name (truncated to 63 chars) */}}
{{- define "nocturne.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/* Fully qualified release name */}}
{{- define "nocturne.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- $name := default .Chart.Name .Values.nameOverride -}}
{{- if contains $name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{- define "nocturne.api.fullname" -}}
{{- printf "%s-api" (include "nocturne.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "nocturne.web.fullname" -}}
{{- printf "%s-web" (include "nocturne.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "nocturne.bootstrap.fullname" -}}
{{- printf "%s-bootstrap" (include "nocturne.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "nocturne.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/* Common labels */}}
{{- define "nocturne.labels" -}}
helm.sh/chart: {{ include "nocturne.chart" . }}
{{ include "nocturne.selectorLabels" . }}
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{- define "nocturne.selectorLabels" -}}
app.kubernetes.io/name: {{ include "nocturne.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- define "nocturne.api.selectorLabels" -}}
{{ include "nocturne.selectorLabels" . }}
app.kubernetes.io/component: api
{{- end -}}

{{- define "nocturne.web.selectorLabels" -}}
{{ include "nocturne.selectorLabels" . }}
app.kubernetes.io/component: web
{{- end -}}

{{- define "nocturne.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{- default (include "nocturne.fullname" .) .Values.serviceAccount.name -}}
{{- else -}}
{{- default "default" .Values.serviceAccount.name -}}
{{- end -}}
{{- end -}}

{{/*
Image references.

When `<component>.image.digest` is set, the image is referenced as
`<registry>/<repository>@<digest>` (deterministic pull, immune to
node-cache staleness on mutable tags like `:latest`). Otherwise falls
back to `<registry>/<repository>:<tag>`.
*/}}
{{- define "nocturne.api.image" -}}
{{- $reg := .Values.image.registry -}}
{{- $repo := .Values.api.image.repository -}}
{{- if .Values.api.image.digest -}}
{{- printf "%s/%s@%s" $reg $repo .Values.api.image.digest -}}
{{- else -}}
{{- $tag := default .Chart.AppVersion .Values.api.image.tag -}}
{{- printf "%s/%s:%s" $reg $repo $tag -}}
{{- end -}}
{{- end -}}

{{- define "nocturne.web.image" -}}
{{- $reg := .Values.image.registry -}}
{{- $repo := .Values.web.image.repository -}}
{{- if .Values.web.image.digest -}}
{{- printf "%s/%s@%s" $reg $repo .Values.web.image.digest -}}
{{- else -}}
{{- $tag := default .Chart.AppVersion .Values.web.image.tag -}}
{{- printf "%s/%s:%s" $reg $repo $tag -}}
{{- end -}}
{{- end -}}

{{- define "nocturne.bootstrap.image" -}}
{{- if .Values.bootstrap.image.digest -}}
{{- printf "%s/%s@%s" .Values.bootstrap.image.registry .Values.bootstrap.image.repository .Values.bootstrap.image.digest -}}
{{- else -}}
{{- printf "%s/%s:%s" .Values.bootstrap.image.registry .Values.bootstrap.image.repository .Values.bootstrap.image.tag -}}
{{- end -}}
{{- end -}}

{{/* Name of the Secret holding the instance key */}}
{{- define "nocturne.instanceKeySecretName" -}}
{{- if .Values.instanceKey.existingSecret -}}
{{- .Values.instanceKey.existingSecret -}}
{{- else -}}
{{- printf "%s-instance-key" (include "nocturne.fullname" .) -}}
{{- end -}}
{{- end -}}

{{- define "nocturne.instanceKeySecretKey" -}}
{{- default "instance-key" .Values.instanceKey.existingSecretKey -}}
{{- end -}}

{{/* Internal API URL used by the web container */}}
{{- define "nocturne.api.internalUrl" -}}
{{- printf "http://%s.%s.svc.cluster.local:%d" (include "nocturne.api.fullname" .) .Release.Namespace (int .Values.api.service.port) -}}
{{- end -}}

{{/* ============================================================
     Database helpers
     ============================================================ */}}

{{- define "nocturne.db.mode" -}}
{{- if .Values.postgresql.enabled -}}bundled{{- else -}}external{{- end -}}
{{- end -}}

{{- define "nocturne.db.host" -}}
{{- if .Values.postgresql.enabled -}}
{{- printf "%s-postgresql" .Release.Name -}}
{{- else -}}
{{- .Values.externalDatabase.host -}}
{{- end -}}
{{- end -}}

{{- define "nocturne.db.port" -}}
{{- if .Values.postgresql.enabled -}}5432{{- else -}}{{ .Values.externalDatabase.port }}{{- end -}}
{{- end -}}

{{- define "nocturne.db.database" -}}
{{- if .Values.postgresql.enabled -}}
{{- default "nocturne" .Values.postgresql.auth.database -}}
{{- else -}}
{{- .Values.externalDatabase.database -}}
{{- end -}}
{{- end -}}

{{- define "nocturne.db.sslMode" -}}
{{- if .Values.postgresql.enabled -}}prefer{{- else -}}{{ .Values.externalDatabase.sslMode }}{{- end -}}
{{- end -}}

{{/* Name of the chart-managed bundled-DB Secret (or user-provided existingSecret) */}}
{{- define "nocturne.bundledDb.secretName" -}}
{{- if and .Values.postgresql.enabled .Values.postgresql.auth.existingSecret -}}
{{- .Values.postgresql.auth.existingSecret -}}
{{- else -}}
{{- printf "%s-db" (include "nocturne.fullname" .) -}}
{{- end -}}
{{- end -}}

{{/*
Returns the Secret name holding a given role's password.
Usage: {{ include "nocturne.db.passwordSecret" (dict "ctx" . "role" "app") }}
Roles: app | migrator | web | postgres (postgres only valid in bundled mode)
*/}}
{{- define "nocturne.db.passwordSecret" -}}
{{- $ctx := .ctx -}}
{{- $role := .role -}}
{{- if $ctx.Values.postgresql.enabled -}}
{{- include "nocturne.bundledDb.secretName" $ctx -}}
{{- else -}}
{{- if eq $role "app" -}}{{ $ctx.Values.externalDatabase.appSecret.existingSecret }}
{{- else if eq $role "migrator" -}}{{ $ctx.Values.externalDatabase.migratorSecret.existingSecret }}
{{- else if eq $role "web" -}}{{ $ctx.Values.externalDatabase.webSecret.existingSecret }}
{{- end -}}
{{- end -}}
{{- end -}}

{{/*
Bitnami's own auth Secret name and key for the postgres superuser password.
Defaults to <release>-postgresql / postgres-password unless the user provided
postgresql.auth.existingSecret + custom secretKeys.
*/}}
{{- define "nocturne.bundledPostgresSuperSecret" -}}
{{- if .Values.postgresql.auth.existingSecret -}}
{{- .Values.postgresql.auth.existingSecret -}}
{{- else -}}
{{- printf "%s-postgresql" .Release.Name -}}
{{- end -}}
{{- end -}}

{{- define "nocturne.bundledPostgresSuperKey" -}}
{{- default "postgres-password" .Values.postgresql.auth.secretKeys.adminPasswordKey -}}
{{- end -}}

{{- define "nocturne.db.passwordKey" -}}
{{- $ctx := .ctx -}}
{{- $role := .role -}}
{{- if $ctx.Values.postgresql.enabled -}}
{{- printf "%s-password" $role -}}
{{- else -}}
{{- if eq $role "app" -}}{{ $ctx.Values.externalDatabase.appSecret.existingSecretKey }}
{{- else if eq $role "migrator" -}}{{ $ctx.Values.externalDatabase.migratorSecret.existingSecretKey }}
{{- else if eq $role "web" -}}{{ $ctx.Values.externalDatabase.webSecret.existingSecretKey }}
{{- end -}}
{{- end -}}
{{- end -}}
