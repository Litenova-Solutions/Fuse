import type { MetadataRoute } from 'next';
import { blogSource, source } from '@/lib/source';
import { siteUrl } from '@/lib/seo';

export default function sitemap(): MetadataRoute.Sitemap {
  const now = new Date();

  const docsPages: MetadataRoute.Sitemap = source.getPages().map((page) => ({
    url: `${siteUrl}${page.url}`,
    lastModified: now,
    changeFrequency: 'weekly',
    priority: page.url === '/docs' ? 0.9 : 0.7,
  }));

  const blogPages: MetadataRoute.Sitemap = blogSource.getPages().map((page) => ({
    url: `${siteUrl}${page.url}`,
    lastModified: new Date(page.data.date),
    changeFrequency: 'monthly',
    priority: 0.6,
  }));

  return [
    {
      url: siteUrl,
      lastModified: now,
      changeFrequency: 'weekly',
      priority: 1,
    },
    {
      url: `${siteUrl}/blog`,
      lastModified: now,
      changeFrequency: 'weekly',
      priority: 0.8,
    },
    ...docsPages,
    ...blogPages,
  ];
}
