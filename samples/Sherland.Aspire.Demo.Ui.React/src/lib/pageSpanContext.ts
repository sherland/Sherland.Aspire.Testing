import { trace, context, type Context, SpanStatusCode, type Span } from '@opentelemetry/api';

const TRACER_NAME = 'demo-ui';
const SETTLE_DELAY_MS = 500;
const INITIAL_SETTLE_DELAY_MS = 3_000;
const MAX_LIFETIME_MS = 30_000;

export type SpanToken = { owner: 'page' | 'interaction'; id: number } | null;

let pageSpan: Span | null = null;
let pageCtx: Context | null = null;
let pageId = 0;
let pageInflight = 0;
let pageSettleTimer: ReturnType<typeof setTimeout> | null = null;
let pageMaxTimer: ReturnType<typeof setTimeout> | null = null;

let interactionSpan: Span | null = null;
let interactionCtx: Context | null = null;
let interactionId = 0;
let interactionInflight = 0;
let interactionSettleTimer: ReturnType<typeof setTimeout> | null = null;
let interactionMaxTimer: ReturnType<typeof setTimeout> | null = null;

function clearPageTimers() {
  if (pageSettleTimer !== null) {
    clearTimeout(pageSettleTimer);
    pageSettleTimer = null;
  }
  if (pageMaxTimer !== null) {
    clearTimeout(pageMaxTimer);
    pageMaxTimer = null;
  }
}

function clearInteractionTimers() {
  if (interactionSettleTimer !== null) {
    clearTimeout(interactionSettleTimer);
    interactionSettleTimer = null;
  }
  if (interactionMaxTimer !== null) {
    clearTimeout(interactionMaxTimer);
    interactionMaxTimer = null;
  }
}

function settlePage() {
  if (!pageSpan) return;
  clearPageTimers();
  pageSpan.setStatus({ code: SpanStatusCode.OK });
  pageSpan.end();
  pageSpan = null;
  pageCtx = null;
  pageInflight = 0;
}

function settleInteraction() {
  if (!interactionSpan) return;
  clearInteractionTimers();
  interactionSpan.setStatus({ code: SpanStatusCode.OK });
  interactionSpan.end();
  interactionSpan = null;
  interactionCtx = null;
  interactionInflight = 0;
}

export function startPageSpan(pathname: string) {
  settlePage();

  const tracer = trace.getTracer(TRACER_NAME);
  const span = tracer.startSpan(`page ${pathname}`, {
    attributes: { 'page.route': pathname },
  });
  pageSpan = span;
  pageCtx = trace.setSpan(context.active(), span);
  pageId++;
  pageInflight = 0;

  pageSettleTimer = setTimeout(settlePage, INITIAL_SETTLE_DELAY_MS);
  pageMaxTimer = setTimeout(settlePage, MAX_LIFETIME_MS);
}

export function endPageSpan() {
  if (pageSpan) settlePage();
}

export function startInteractionSpan(
  name: string,
  attrs?: Record<string, string | number | boolean>,
): void {
  settleInteraction();

  const tracer = trace.getTracer(TRACER_NAME);
  const span = tracer.startSpan(name, { attributes: attrs });
  interactionSpan = span;
  interactionCtx = trace.setSpan(context.active(), span);
  interactionId++;
  interactionInflight = 0;

  interactionSettleTimer = setTimeout(settleInteraction, INITIAL_SETTLE_DELAY_MS);
  interactionMaxTimer = setTimeout(settleInteraction, MAX_LIFETIME_MS);
}

export function endInteractionSpan() {
  if (interactionSpan) settleInteraction();
}

export function getPageContext(): Context {
  return interactionCtx ?? pageCtx ?? context.active();
}

export function notifyApiCallStart(): SpanToken {
  if (interactionSpan) {
    if (interactionSettleTimer !== null) {
      clearTimeout(interactionSettleTimer);
      interactionSettleTimer = null;
    }
    interactionInflight++;
    return { owner: 'interaction', id: interactionId };
  }
  if (pageSpan) {
    if (pageSettleTimer !== null) {
      clearTimeout(pageSettleTimer);
      pageSettleTimer = null;
    }
    pageInflight++;
    return { owner: 'page', id: pageId };
  }
  return null;
}

export function notifyApiCallEnd(token: SpanToken) {
  if (token === null) return;

  if (token.owner === 'interaction') {
    if (!interactionSpan || token.id !== interactionId) return;
    interactionInflight = Math.max(0, interactionInflight - 1);
    if (interactionInflight === 0) {
      interactionSettleTimer = setTimeout(settleInteraction, SETTLE_DELAY_MS);
    }
    return;
  }

  if (!pageSpan || token.id !== pageId) return;
  pageInflight = Math.max(0, pageInflight - 1);
  if (pageInflight === 0) {
    pageSettleTimer = setTimeout(settlePage, SETTLE_DELAY_MS);
  }
}
