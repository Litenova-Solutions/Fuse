import type { MetadataRoute } from 'next';
import { siteUrl } from '@/lib/seo';

export default function robots(): MetadataRoute.Robots {
  return {
    rules: {
      userAgent: '*',
      allow: '/',
      disallow: ['/llms.mdx/'],
    },
    sitemap: `${siteUrl}/sitemap.xml`,
  };
}
