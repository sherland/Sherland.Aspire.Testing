import { WebTracerProvider, StackContextManager } from '@opentelemetry/sdk-trace-web';
import {
  BatchSpanProcessor,
  AlwaysOnSampler,
  SimpleSpanProcessor,
  type ReadableSpan,
  type SpanExporter,
} from '@opentelemetry/sdk-trace-base';
import { OTLPTraceExporter } from '@opentelemetry/exporter-trace-otlp-http';
import { resourceFromAttributes } from '@opentelemetry/resources';
import { ATTR_SERVICE_NAME } from '@opentelemetry/semantic-conventions';
import { registerInstrumentations } from '@opentelemetry/instrumentation';
import { FetchInstrumentation } from '@opentelemetry/instrumentation-fetch';

let provider: WebTracerProvider | null = null;

class BrowserConsoleSpanExporter implements SpanExporter {
  export(spans: ReadableSpan[], resultCallback: (result: { code: 0 | 1 }) => void): void {
    for (const span of spans) {
      const details: string[] = [];
      appendAttribute(details, span, 'http.request.method');
      appendAttribute(details, span, 'http.response.status_code');
      appendAttribute(details, span, 'url.full');
      appendAttribute(details, span, 'server.address');
      appendAttribute(details, span, 'page.route');

      console.info(
        `[otel-trace] service=demo-ui span="${span.name}" kind=${span.kind} duration_ms=${formatDurationMs(span)} trace=${span.spanContext().traceId} span_id=${span.spanContext().spanId}${details.length > 0 ? ` ${details.join(' ')}` : ''}`,
      );
    }

    resultCallback({ code: 0 });
  }

  forceFlush(): Promise<void> {
    return Promise.resolve();
  }

  shutdown(): Promise<void> {
    return Promise.resolve();
  }
}

function appendAttribute(details: string[], span: ReadableSpan, key: string) {
  const value = span.attributes[key];
  if (value !== undefined) {
    details.push(`${key}=${String(value)}`);
  }
}

function formatDurationMs(span: ReadableSpan): string {
  const [seconds, nanos] = span.duration;
  return (seconds * 1_000 + nanos / 1_000_000).toFixed(1);
}

export function initInstrumentation(): void {
  if (typeof window === 'undefined') return;
  if (provider) return;

  const otlpEndpoint = import.meta.env.VITE_OTEL_EXPORTER_OTLP_ENDPOINT as string | undefined;
  if (!otlpEndpoint) {
    console.debug('[OTel] VITE_OTEL_EXPORTER_OTLP_ENDPOINT not set - tracing disabled');
    return;
  }

  const headersStr = import.meta.env.VITE_OTEL_EXPORTER_OTLP_HEADERS as string | undefined;
  const headers: Record<string, string> = {};
  if (headersStr) {
    for (const pair of headersStr.split(',')) {
      const idx = pair.indexOf('=');
      if (idx > 0) headers[pair.slice(0, idx).trim()] = pair.slice(idx + 1).trim();
    }
  }

  const apiBaseUrl = import.meta.env.VITE_API_BASE_URL as string | undefined;
  const enableConsoleTrace = import.meta.env.VITE_OTEL_TRACE_TO_CONSOLE === '1';
  let apiOriginPattern: RegExp | undefined;
  if (apiBaseUrl) {
    try {
      const { host } = new URL(apiBaseUrl);
      apiOriginPattern = new RegExp(
        `https?://${host.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}`,
        'i',
      );
    } catch {
      // ignore bad URL
    }
  }

  const exporter = new OTLPTraceExporter({
    url: `${otlpEndpoint}/v1/traces`,
    headers,
  });

  provider = new WebTracerProvider({
    resource: resourceFromAttributes({ [ATTR_SERVICE_NAME]: 'demo-ui' }),
    sampler: new AlwaysOnSampler(),
    spanProcessors: [
      new BatchSpanProcessor(exporter, {
        scheduledDelayMillis: 1000,
        maxExportBatchSize: 50,
      }),
      ...(enableConsoleTrace ? [new SimpleSpanProcessor(new BrowserConsoleSpanExporter())] : []),
    ],
  });

  provider.register({ contextManager: new StackContextManager() });

  registerInstrumentations({
    instrumentations: [
      new FetchInstrumentation({
        propagateTraceHeaderCorsUrls: apiOriginPattern ? [apiOriginPattern] : [],
        ignoreUrls: [/\/v1\/traces/],
      }),
    ],
  });

  window.addEventListener('beforeunload', () => {
    provider?.forceFlush();
  });
}
