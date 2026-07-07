import { context } from '@opentelemetry/api';
import { getPageContext, notifyApiCallStart, notifyApiCallEnd } from './pageSpanContext';

export async function tracedFetch(url: string, init?: RequestInit): Promise<Response> {
  const token = notifyApiCallStart();
  try {
    return await context.with(getPageContext(), () => fetch(url, init));
  } finally {
    notifyApiCallEnd(token);
  }
}
