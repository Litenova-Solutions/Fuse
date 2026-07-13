import { defineConfig, defineDocs } from 'fumadocs-mdx/config';
import { metaSchema, pageSchema } from 'fumadocs-core/source/schema';
import { z } from 'zod';

// You can customize Zod schemas for frontmatter and `meta.json` here
// see https://fumadocs.dev/docs/mdx/collections
export const docs = defineDocs({
  dir: 'content/docs',
  docs: {
    schema: pageSchema,
    postprocess: {
      includeProcessedMarkdown: true,
    },
  },
  meta: {
    schema: metaSchema,
  },
});

// The blog collection (marketing plan M5): launch posts under /blog. The page schema is
// extended with a publication date and an author so the index can sort and attribute posts.
export const blog = defineDocs({
  dir: 'content/blog',
  docs: {
    schema: pageSchema.extend({
      date: z.string(),
      author: z.string().optional(),
    }),
  },
  meta: {
    schema: metaSchema,
  },
});

export default defineConfig({
  mdxOptions: {
    // MDX options
  },
});
