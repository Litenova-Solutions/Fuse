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
    default: 'Fuse - local .NET index and compiler verification for coding agents',
  },
  description:
    'Fuse indexes a .NET solution locally, resolves DI and routes from a typed graph, packs branch context, and typechecks proposed edits through the compiler before write.',
  icons: {
    icon: [
      { url: '/fuse-icon.svg', type: 'image/svg+xml' },
      { url: '/fuse-icon.png', type: 'image/png', sizes: '128x128' },
    ],
    apple: { url: '/fuse-icon-256.png', sizes: '256x256', type: 'image/png' },
  },
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
