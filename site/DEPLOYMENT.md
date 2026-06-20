# Deploying the Fuse site to Vercel

This site is a Next.js (App Router) app built with Fumadocs. It lives in the `site/`
subdirectory of the Fuse repository and does not touch the .NET solution.

## One-time Vercel project setup

1. In the Vercel dashboard, **Add New Project** and import the
   `Litenova-Solutions/Fuse` repository.
2. Set **Root Directory** to `site`. This is the only non-default setting; without it
   Vercel looks for the app at the repository root and the build fails.
3. Framework preset is detected as **Next.js**. The build command (`next build`),
   install command (`npm install`), and output are configured in `site/vercel.json`,
   so the defaults are correct.
4. Deploy. Vercel builds `main` on every push (see the `git` block in `vercel.json`)
   and gives every other branch a preview URL.

No environment variables are required for the site to build or run.

## Custom domain: fuse.codes

The domain is **not purchased yet**. Once it is registered, add it to the Vercel
project and set the DNS records below at the registrar.

1. In the Vercel project, open **Settings -> Domains** and add `fuse.codes` and
   `www.fuse.codes`.
2. Vercel will show the exact target values to use; the records below are the standard
   Vercel set. Apply them at the DNS provider for `fuse.codes`.

### DNS records to set

| Type | Name (host) | Value | Purpose |
|------|-------------|-------|---------|
| A | `@` (apex) | `76.76.21.21` | Points the apex `fuse.codes` at Vercel. |
| CNAME | `www` | `cname.vercel-dns.com` | Points `www.fuse.codes` at Vercel. |

Notes:

- Use the apex `A` value (or the `ALIAS`/`ANAME` target) that the Vercel Domains page
  displays at the time you add the domain; Vercel occasionally updates the apex target.
  If your DNS provider supports `ALIAS`/`ANAME` flattening, an `ALIAS @ ->
  cname.vercel-dns.com` is preferred over the bare `A` record.
- Pick one canonical host in **Settings -> Domains** (the apex `fuse.codes` is the
  recommended canonical) and let Vercel redirect the other to it.
- TLS certificates are issued automatically by Vercel once the records resolve.
- Set the production site URL to `https://fuse.codes`. It is already used as
  `metadataBase` in `src/app/layout.tsx` for canonical and Open Graph URLs.

## Local development

```bash
cd site
npm install
npm run dev     # http://localhost:3000
npm run build   # production build
```
