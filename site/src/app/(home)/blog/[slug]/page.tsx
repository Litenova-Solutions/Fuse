import Link from 'next/link';
import type { Metadata } from 'next';
import { notFound } from 'next/navigation';
import { createRelativeLink } from 'fumadocs-ui/mdx';
import { DocsBody } from 'fumadocs-ui/layouts/docs/page';
import { blogSource } from '@/lib/source';
import { getMDXComponents } from '@/components/mdx';
import { absoluteUrl, articleJsonLd } from '@/lib/seo';

function formatDate(d: string) {
  return new Date(d).toLocaleDateString('en-US', {
    year: 'numeric',
    month: 'long',
    day: 'numeric',
  });
}

export default async function Page(props: PageProps<'/blog/[slug]'>) {
  const params = await props.params;
  const page = blogSource.getPage([params.slug]);
  if (!page) notFound();

  const MDX = page.data.body;
  const author = page.data.author;

  return (
    <main className="mx-auto w-full max-w-3xl px-6 py-16">
      <script
        type="application/ld+json"
        dangerouslySetInnerHTML={{
          __html: JSON.stringify(
            articleJsonLd({
              title: page.data.title,
              description: page.data.description ?? '',
              url: page.url,
              datePublished: page.data.date,
              author: author ?? undefined,
            }),
          ),
        }}
      />
      <Link href="/blog" className="text-sm text-fd-muted-foreground hover:text-fd-foreground">
        Back to blog
      </Link>
      <article className="mt-6">
        <div className="text-xs uppercase tracking-wide text-fd-muted-foreground">
          <time dateTime={page.data.date}>{formatDate(page.data.date)}</time>
          {author ? ` . ${author}` : ''}
        </div>
        <h1 className="mt-2 text-3xl font-bold tracking-tight">{page.data.title}</h1>
        <p className="mt-3 text-lg text-fd-muted-foreground">{page.data.description}</p>
        <DocsBody className="mt-8">
          <MDX components={getMDXComponents({ a: createRelativeLink(blogSource, page) })} />
        </DocsBody>
      </article>
    </main>
  );
}

export function generateStaticParams() {
  return blogSource.getPages().map((page) => ({ slug: page.slugs[0] }));
}

export async function generateMetadata(props: PageProps<'/blog/[slug]'>): Promise<Metadata> {
  const params = await props.params;
  const page = blogSource.getPage([params.slug]);
  if (!page) notFound();

  const canonical = absoluteUrl(page.url);

  return {
    title: page.data.title,
    description: page.data.description,
    alternates: {
      canonical,
    },
    openGraph: {
      type: 'article',
      url: canonical,
      title: page.data.title,
      description: page.data.description,
      siteName: 'Fuse',
      publishedTime: page.data.date,
      authors: page.data.author ? [page.data.author] : ['Fuse'],
      images: [{ url: '/fuse-social-card-v2.png', width: 1200, height: 628, alt: page.data.title }],
    },
    twitter: {
      card: 'summary_large_image',
      title: page.data.title,
      description: page.data.description,
      images: [{ url: '/fuse-social-card-v2.png', alt: page.data.title }],
    },
  };
}
