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

{{/* Image references */}}
{{- define "nocturne.api.image" -}}
{{- $reg := .Values.image.registry -}}
{{- $repo := .Values.api.image.repository -}}
{{- $tag := default .Chart.AppVersion .Values.api.image.tag -}}
{{- printf "%s/%s:%s" $reg $repo $tag -}}
{{- end -}}

{{- define "nocturne.web.image" -}}
{{- $reg := .Values.image.registry -}}
{{- $repo := .Values.web.image.repository -}}
{{- $tag := default .Chart.AppVersion .Values.web.image.tag -}}
{{- printf "%s/%s:%s" $reg $repo $tag -}}
{{- end -}}

{{- define "nocturne.bootstrap.image" -}}
{{- printf "%s/%s:%s" .Values.bootstrap.image.registry .Values.bootstrap.image.repository .Values.bootstrap.image.tag -}}
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
