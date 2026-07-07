import { useEffect, useMemo, useState } from 'react';
import { endInteractionSpan, endPageSpan, startInteractionSpan, startPageSpan } from './lib/pageSpanContext';
import { tracedFetch } from './lib/tracedFetch';

type DemoItem = {
  id: number;
  name: string;
  category: string;
};

type ProcessResult = {
  processed: boolean;
  id: number;
  message: string;
};

const apiBaseUrl = import.meta.env.VITE_API_BASE_URL as string | undefined;

export function App() {
  const [items, setItems] = useState<DemoItem[]>([]);
  const [status, setStatus] = useState('Loading items...');
  const [lastResult, setLastResult] = useState<ProcessResult | null>(null);

  const itemsUrl = useMemo(() => new URL('/items', apiBaseUrl).toString(), []);

  useEffect(() => {
    startPageSpan('/dashboard');

    let disposed = false;
    (async () => {
      try {
        const response = await tracedFetch(itemsUrl);
        if (!response.ok) {
          throw new Error(`GET /items failed: ${response.status}`);
        }

        const data = (await response.json()) as DemoItem[];
        if (!disposed) {
          setItems(data);
          setStatus(`Loaded ${data.length} items`);
        }
      } catch (error) {
        if (!disposed) {
          const message = error instanceof Error ? error.message : String(error);
          setStatus(`Failed to load items: ${message}`);
        }
      }
    })();

    return () => {
      disposed = true;
      endPageSpan();
      endInteractionSpan();
    };
  }, [itemsUrl]);

  async function processItem(id: number) {
    startInteractionSpan('item:process', { 'item.id': id });
    setStatus(`Processing item ${id}...`);

    try {
      const response = await tracedFetch(new URL(`/items/${id}/process`, apiBaseUrl).toString(), {
        method: 'POST',
      });

      if (!response.ok) {
        throw new Error(`POST /items/${id}/process failed: ${response.status}`);
      }

      const result = (await response.json()) as ProcessResult;
      setLastResult(result);
      setStatus(result.message);
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      setStatus(`Failed to process item ${id}: ${message}`);
    }
  }

  return (
    <main style={{ fontFamily: 'Segoe UI, sans-serif', maxWidth: 900, margin: '2rem auto', lineHeight: 1.5 }}>
      <h1 data-testid="page-title">Sherland Aspire Demo UI</h1>
      <p data-testid="status-line">{status}</p>

      <ul data-testid="items-list" style={{ padding: 0, listStyle: 'none', display: 'grid', gap: '0.75rem' }}>
        {items.map(item => (
          <li key={item.id} data-testid={`item-${item.id}`} style={{ border: '1px solid #d0d7de', borderRadius: 8, padding: '0.75rem' }}>
            <strong>{item.name}</strong>
            <div style={{ marginBottom: '0.5rem' }}>Category: {item.category}</div>
            <button
              data-testid={`process-item-${item.id}`}
              type="button"
              onClick={() => processItem(item.id)}
            >
              Process item
            </button>
          </li>
        ))}
      </ul>

      {lastResult && (
        <section data-testid="process-result" style={{ marginTop: '1rem', padding: '0.75rem', background: '#f6f8fa', borderRadius: 8 }}>
          Last result: item {lastResult.id}, processed={String(lastResult.processed)}
        </section>
      )}
    </main>
  );
}
