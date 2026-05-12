{{/*
Observability env vars for the API container.

Nocturne's API uses Aspire ServiceDefaults' OpenTelemetry plumbing
(src/Aspire/Nocturne.Aspire.ServiceDefaults/Extensions.cs:97-104), which
registers the OTLP exporter only when OTEL_EXPORTER_OTLP_ENDPOINT is set.
We follow that contract: when observability.otlp.enabled is true, set the
endpoint plus standard OTel SDK env vars; when false, set nothing.

The web container has no OpenTelemetry SDK and is intentionally omitted.
*/}}
{{- define "nocturne.observability.api.env" -}}
{{- if .Values.observability.otlp.enabled }}
- name: OTEL_EXPORTER_OTLP_ENDPOINT
  value: {{ required "observability.otlp.endpoint is required when observability.otlp.enabled=true" .Values.observability.otlp.endpoint | quote }}
- name: OTEL_EXPORTER_OTLP_PROTOCOL
  value: {{ .Values.observability.otlp.protocol | quote }}
- name: OTEL_SERVICE_NAME
  value: {{ default (include "nocturne.api.fullname" .) .Values.observability.otlp.serviceName | quote }}
# k8s.* attributes via downward API; referenced by OTEL_RESOURCE_ATTRIBUTES below.
- name: K8S_NAMESPACE
  valueFrom:
    fieldRef:
      fieldPath: metadata.namespace
- name: K8S_POD_NAME
  valueFrom:
    fieldRef:
      fieldPath: metadata.name
- name: OTEL_RESOURCE_ATTRIBUTES
  value: "k8s.namespace.name=$(K8S_NAMESPACE),k8s.pod.name=$(K8S_POD_NAME),service.version={{ default .Chart.AppVersion .Values.api.image.tag }}{{- range $k, $v := .Values.observability.otlp.resourceAttributes }},{{ $k }}={{ $v }}{{- end }}"
{{- if .Values.observability.otlp.headersSecretRef.name }}
- name: OTEL_EXPORTER_OTLP_HEADERS
  valueFrom:
    secretKeyRef:
      name: {{ .Values.observability.otlp.headersSecretRef.name }}
      key: {{ .Values.observability.otlp.headersSecretRef.key }}
{{- end }}
# Aspire AppHost defaults from aspire-manifest.json — pass through unchanged
# so production matches local AppHost behavior.
- name: OTEL_DOTNET_EXPERIMENTAL_OTLP_EMIT_EXCEPTION_LOG_ATTRIBUTES
  value: "true"
- name: OTEL_DOTNET_EXPERIMENTAL_OTLP_EMIT_EVENT_LOG_ATTRIBUTES
  value: "true"
- name: OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY
  value: "in_memory"
{{- range $name, $value := .Values.observability.otlp.extraEnv }}
- name: {{ $name }}
  value: {{ $value | quote }}
{{- end }}
{{- end }}
{{- end -}}
