'use client';

import { useTheme } from 'next-themes';
import { useEffect, useState } from 'react';
import { cn } from '@/lib/cn';

type DiagramProps = {
  src: string;
  alt: string;
  className?: string;
};

// Inline public SVG figures so class-based dark mode can reach their CSS variables.
export function Diagram({ src, alt, className }: DiagramProps) {
  const { resolvedTheme } = useTheme();
  const [markup, setMarkup] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    async function load() {
      try {
        const response = await fetch(src);
        if (!response.ok) {
          throw new Error(`${response.status} ${response.statusText}`);
        }
        const text = await response.text();
        if (!cancelled) {
          setMarkup(text);
          setError(null);
        }
      } catch (e) {
        if (!cancelled) {
          setError(e instanceof Error ? e.message : 'Failed to load diagram');
        }
      }
    }

    void load();
    return () => {
      cancelled = true;
    };
  }, [src]);

  if (error) {
    return (
      <p className="my-4 text-sm text-fd-muted-foreground">
        Diagram unavailable ({src}): {error}
      </p>
    );
  }

  if (!markup) {
    return (
      <div className="my-6 flex min-h-[120px] items-center justify-center rounded-lg border border-fd-border bg-fd-card/50 text-sm text-fd-muted-foreground">
        Loading diagram...
      </div>
    );
  }

  const themedMarkup = markup.replace(
    /<svg\b([^>]*)>/,
    `<svg$1 class="${resolvedTheme === 'dark' ? 'dark' : ''}" role="img" aria-label="${alt}">`,
  );

  return (
    <div
      className={cn(
        'diagram my-6 overflow-x-auto rounded-xl border border-fd-border bg-fd-background p-4 [&_svg]:mx-auto [&_svg]:h-auto [&_svg]:max-w-full',
        className,
      )}
      dangerouslySetInnerHTML={{ __html: themedMarkup }}
    />
  );
}
