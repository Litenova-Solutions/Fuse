export const siteUrl = 'https://fuse.codes';

export function absoluteUrl(path: string): string {
  if (path.startsWith('http')) return path;
  return `${siteUrl}${path.startsWith('/') ? path : `/${path}`}`;
}

export function breadcrumbJsonLd(items: { name: string; url: string }[]) {
  return {
    '@context': 'https://schema.org',
    '@type': 'BreadcrumbList',
    itemListElement: items.map((item, index) => ({
      '@type': 'ListItem',
      position: index + 1,
      name: item.name,
      item: absoluteUrl(item.url),
    })),
  };
}

export function articleJsonLd(input: {
  title: string;
  description: string;
  url: string;
  datePublished: string;
  author?: string;
}) {
  return {
    '@context': 'https://schema.org',
    '@type': 'Article',
    headline: input.title,
    description: input.description,
    url: absoluteUrl(input.url),
    datePublished: input.datePublished,
    author: input.author
      ? { '@type': 'Organization', name: input.author }
      : { '@type': 'Organization', name: 'Fuse' },
    publisher: {
      '@type': 'Organization',
      name: 'Fuse',
      url: siteUrl,
    },
  };
}
