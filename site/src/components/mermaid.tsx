'use client';

import { useEffect, useId, useRef, useState } from 'react';

export function Mermaid({ chart }: { chart: string }) {
  const id = useId().replace(/:/g, '');
  const containerRef = useRef<HTMLDivElement>(null);
  const [svg, setSvg] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    async function render() {
      try {
        const mermaid = (await import('mermaid')).default;
        mermaid.initialize({
          startOnLoad: false,
          theme: 'neutral',
          securityLevel: 'strict',
        });
        const { svg: rendered } = await mermaid.render(`mermaid-${id}`, chart.trim());
        if (!cancelled) {
          setSvg(rendered);
          setError(null);
        }
      } catch (e) {
        if (!cancelled) {
          setError(e instanceof Error ? e.message : 'Failed to render diagram');
        }
      }
    }

    void render();
    return () => {
      cancelled = true;
    };
  }, [chart, id]);

  if (error) {
    return (
      <pre className="overflow-x-auto rounded-lg border border-fd-border bg-fd-card p-4 font-mono text-sm">
        {chart}
      </pre>
    );
  }

  if (!svg) {
    return (
      <div
        ref={containerRef}
        className="my-4 flex min-h-[120px] items-center justify-center rounded-lg border border-fd-border bg-fd-card/50 text-sm text-fd-muted-foreground"
      >
        Loading diagram...
      </div>
    );
  }

  return (
    <div
      ref={containerRef}
      className="my-4 overflow-x-auto rounded-lg border border-fd-border bg-fd-card p-4 [&_svg]:mx-auto"
      dangerouslySetInnerHTML={{ __html: svg }}
    />
  );
}
