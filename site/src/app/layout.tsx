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
    default: 'Fuse - .NET-native context optimizer for AI agents',
  },
  description:
    'Fuse turns a .NET codebase into one token-efficient payload an AI agent reads in a single call. Fewer tokens, the right files, and the public API kept intact.',
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
