import Link from 'next/link';
import { FuseMark } from '@/components/fuse-mark';
import { appName, githubUrl } from '@/lib/shared';

const footerColumns = [
  {
    title: 'Start',
    links: [
      { label: 'Install', href: '/docs/start/install' },
      { label: 'Quickstart', href: '/docs/start/quickstart' },
      { label: 'Connect your agent', href: '/docs/start/connect-your-ai' },
      { label: 'Why Fuse', href: '/docs/start/why-fuse' },
    ],
  },
  {
    title: 'Docs',
    links: [
      { label: 'Overview', href: '/docs' },
      { label: 'MCP tools', href: '/docs/reference/mcp-tools' },
      { label: 'Commands', href: '/docs/reference/commands' },
      { label: 'Benchmarks', href: '/docs/project/benchmarks' },
    ],
  },
  {
    title: 'Project',
    links: [
      { label: 'GitHub', href: githubUrl, external: true },
      { label: 'NuGet', href: 'https://www.nuget.org/packages/Fuse', external: true },
      { label: 'Blog', href: '/blog' },
      { label: 'Changelog', href: '/docs/project/changelog' },
    ],
  },
] as const;

export function SiteFooter() {
  return (
    <footer className="site-footer border-t border-fd-border bg-fd-card/40">
      <div className="site-footer__inner mx-auto w-full max-w-6xl px-6 py-14">
        <div className="site-footer__grid">
          <div className="site-footer__brand">
            <Link href="/" className="site-footer__logo">
              <FuseMark className="size-5" />
              <span>{appName}</span>
            </Link>
            <p className="mt-4 max-w-xs text-sm leading-6 text-fd-muted-foreground">
              Local semantic index and compiler verification for .NET coding agents.
            </p>
          </div>

          {footerColumns.map((column) => (
            <div key={column.title} className="site-footer__column">
              <h2 className="site-footer__heading">{column.title}</h2>
              <ul className="site-footer__links">
                {column.links.map((link) => (
                  <li key={link.href}>
                    {'external' in link && link.external ? (
                      <a
                        href={link.href}
                        className="site-footer__link"
                        target="_blank"
                        rel="noreferrer"
                      >
                        {link.label}
                      </a>
                    ) : (
                      <Link href={link.href} className="site-footer__link">
                        {link.label}
                      </Link>
                    )}
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </div>

        <div className="site-footer__bottom">
          <p className="text-sm text-fd-muted-foreground">
            Apache 2.0. Source and index stay on your machine.
          </p>
          <p className="text-sm text-fd-muted-foreground">
            &copy; {new Date().getFullYear()} Litenova Solutions
          </p>
        </div>
      </div>
    </footer>
  );
}
