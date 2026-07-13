import type { BaseLayoutProps } from 'fumadocs-ui/layouts/shared';
import { appName, githubUrl } from './shared';
import { FuseMark } from '@/components/fuse-mark';

/**
 * Shared layout options for the home and docs layouts: the nav title, links,
 * and the GitHub button.
 */
export function baseOptions(): BaseLayoutProps {
  return {
    nav: {
      title: (
        <>
          <FuseMark className="size-5" />
          <span className="font-semibold">{appName}</span>
        </>
      ),
    },
    links: [
      {
        text: 'Docs',
        url: '/docs',
        active: 'nested-url',
      },
      {
        text: 'Why Fuse',
        url: '/docs/start/why-fuse',
      },
      {
        text: 'Benchmarks',
        url: '/docs/project/benchmarks',
      },
      {
        text: 'Blog',
        url: '/blog',
        active: 'nested-url',
      },
    ],
    githubUrl,
  };
}
