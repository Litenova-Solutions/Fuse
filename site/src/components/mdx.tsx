import defaultMdxComponents from 'fumadocs-ui/mdx';
import { Card, Cards } from 'fumadocs-ui/components/card';
import type { MDXComponents } from 'mdx/types';
import { Mermaid } from '@/components/mermaid';
import { isValidElement, type ReactNode } from 'react';

function PreWithMermaid({ children, ...props }: React.ComponentProps<'pre'>) {
  const child = children as ReactNode;
  if (isValidElement(child) && child.type === 'code') {
    const codeProps = child.props as { className?: string; children?: ReactNode };
    if (codeProps.className?.includes('language-mermaid')) {
      const chart =
        typeof codeProps.children === 'string'
          ? codeProps.children
          : String(codeProps.children ?? '');
      return <Mermaid chart={chart} />;
    }
  }

  return <pre {...props}>{children}</pre>;
}

export function getMDXComponents(components?: MDXComponents) {
  return {
    ...defaultMdxComponents,
    Card,
    Cards,
    pre: PreWithMermaid,
    ...components,
  } satisfies MDXComponents;
}

export const useMDXComponents = getMDXComponents;

declare global {
  type MDXProvidedComponents = ReturnType<typeof getMDXComponents>;
}
