import defaultMdxComponents from 'fumadocs-ui/mdx';
import { Card, Cards } from 'fumadocs-ui/components/card';
import { CodeBlock, Pre } from 'fumadocs-ui/components/codeblock';
import type { MDXComponents } from 'mdx/types';
import { Mermaid } from '@/components/mermaid';
import { Diagram } from '@/components/diagram';
import { isValidElement, type ReactNode } from 'react';

function getTextContent(node: ReactNode): string {
  if (typeof node === 'string') return node;
  if (typeof node === 'number') return String(node);
  if (Array.isArray(node)) return node.map(getTextContent).join('');
  if (isValidElement(node)) {
    const props = node.props as { children?: ReactNode };
    return getTextContent(props.children);
  }
  return '';
}

function isMermaidClassName(className?: string): boolean {
  if (!className) return false;
  return className.includes('language-mermaid') || /\bmermaid\b/.test(className);
}

function extractMermaidChart(node: ReactNode): string | null {
  if (!isValidElement(node)) return null;

  const props = node.props as {
    className?: string;
    children?: ReactNode;
    'data-language'?: string;
  };

  if (isMermaidClassName(props.className) || props['data-language'] === 'mermaid') {
    const chart = getTextContent(props.children).trim();
    if (chart.length > 0) {
      return chart;
    }
  }

  if (props.children) {
    const children = Array.isArray(props.children) ? props.children : [props.children];
    for (const child of children) {
      const chart = extractMermaidChart(child);
      if (chart) return chart;
    }
  }

  return null;
}

function isMermaidChartText(text: string): boolean {
  return /^\s*(flowchart|graph|sequenceDiagram|classDiagram|stateDiagram|erDiagram|gantt|pie|mindmap|timeline|gitGraph|C4Context)\b/.test(
    text,
  );
}

function PreWithMermaid(props: React.ComponentProps<'pre'> & { 'data-language'?: string }) {
  let chart =
    extractMermaidChart(props.children) ??
    (isMermaidClassName(props.className) || props['data-language'] === 'mermaid'
      ? getTextContent(props.children).trim()
      : null);

  if (!chart) {
    const text = getTextContent(props.children).trim();
    if (isMermaidChartText(text)) {
      chart = text;
    }
  }

  if (chart) {
    return <Mermaid chart={chart} />;
  }

  return (
    <CodeBlock {...props}>
      <Pre>{props.children}</Pre>
    </CodeBlock>
  );
}

function CodeWithMermaid(props: React.ComponentProps<'code'> & { 'data-language'?: string }) {
  let chart: string | null = null;

  if (isMermaidClassName(props.className) || props['data-language'] === 'mermaid') {
    chart = getTextContent(props.children).trim();
  } else {
    const text = getTextContent(props.children).trim();
    if (isMermaidChartText(text)) {
      chart = text;
    }
  }

  if (chart) {
    return <Mermaid chart={chart} />;
  }

  return <code {...props} />;
}

function ImgWithDiagram(props: React.ComponentProps<'img'>) {
  const src = typeof props.src === 'string' ? props.src : '';
  if (src.startsWith('/fuse-') && src.endsWith('.svg')) {
    return <Diagram src={src} alt={props.alt ?? ''} className={props.className} />;
  }

  const DefaultImg = defaultMdxComponents.img;
  if (DefaultImg) {
    return <DefaultImg {...props} />;
  }

  return <img {...props} />;
}

export function getMDXComponents(components?: MDXComponents) {
  return {
    ...defaultMdxComponents,
    Card,
    Cards,
    Diagram,
    pre: PreWithMermaid,
    code: CodeWithMermaid,
    img: ImgWithDiagram,
    ...components,
  } satisfies MDXComponents;
}

export const useMDXComponents = getMDXComponents;

declare global {
  type MDXProvidedComponents = ReturnType<typeof getMDXComponents>;
}
