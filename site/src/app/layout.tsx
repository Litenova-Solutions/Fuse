import { RootProvider } from 'fumadocs-ui/provider/next';
import './global.css';
import { Inter } from 'next/font/google';
import type { Metadata } from 'next';

const inter = Inter({
  subsets: ['latin'],
});

export const metadata: Metadata = {
  metadataBase: new URL('https://fuse.codes'),
  title: {
    template: '%s | Fuse',
    default: 'Fuse - typecheck your AI agent\'s .NET edits before they land',
  },
  description:
    'Fuse is an MCP server for .NET that typechecks a proposed edit against the compiler before your agent writes it, resolves DI and route wiring from Roslyn, and scopes a pull request to the files that matter.',
};

export default function Layout({ children }: LayoutProps<'/'>) {
  return (
    <html lang="en" className={inter.className} suppressHydrationWarning>
      <body className="flex flex-col min-h-screen">
        <RootProvider>{children}</RootProvider>
      </body>
    </html>
  );
}
