import Link from 'next/link';
import type { Metadata } from 'next';
import { blogSource } from '@/lib/source';
import { absoluteUrl } from '@/lib/seo';

export const metadata: Metadata = {
  title: 'Blog',
  description: 'Notes from the Fuse team on method, measurement, and releases.',
  alternates: {
    canonical: absoluteUrl('/blog'),
  },
  openGraph: {
    type: 'website',
    url: absoluteUrl('/blog'),
    title: 'Fuse Blog',
    description: 'Notes from the Fuse team on method, measurement, and releases.',
    siteName: 'Fuse',
    images: [{ url: '/fuse-social-card.png', width: 1280, height: 640, alt: 'Fuse' }],
  },
};

function formatDate(d: string) {
  return new Date(d).toLocaleDateString('en-US', {
    year: 'numeric',
    month: 'long',
    day: 'numeric',
  });
}

export default function BlogIndex() {
  const posts = [...blogSource.getPages()].sort(
    (a, b) => new Date(b.data.date).getTime() - new Date(a.data.date).getTime(),
  );

  return (
    <main className="mx-auto w-full max-w-3xl px-6 py-16">
      <h1 className="text-3xl font-bold tracking-tight">Blog</h1>
      <p className="mt-3 text-fd-muted-foreground">
        Notes on method, measurement, and releases.
      </p>
      <div className="mt-10 flex flex-col divide-y divide-fd-border border-y border-fd-border">
        {posts.map((post) => (
          <Link key={post.url} href={post.url} className="group py-6">
            <div className="text-xs uppercase tracking-wide text-fd-muted-foreground">
              {formatDate(post.data.date)}
            </div>
            <h2 className="mt-1 text-xl font-semibold group-hover:text-[var(--brand)]">
              {post.data.title}
            </h2>
            <p className="mt-2 text-sm text-fd-muted-foreground">{post.data.description}</p>
          </Link>
        ))}
      </div>
    </main>
  );
}
