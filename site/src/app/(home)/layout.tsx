'use client';

import type { ComponentProps } from 'react';
import { HomeLayout } from 'fumadocs-ui/layouts/home';
import { cn } from '@/lib/cn';
import { SiteFooter } from '@/components/site-footer';
import { baseOptions } from '@/lib/layout.shared';

function HomeShell({ children, className, ...props }: ComponentProps<'main'>) {
  return (
    <main
      id="nd-home-layout"
      {...props}
      className={cn('flex flex-1 flex-col [--fd-layout-width:1400px]', className)}
    >
      <div className="flex flex-1 flex-col">{children}</div>
      <SiteFooter />
    </main>
  );
}

export default function Layout({ children }: LayoutProps<'/'>) {
  return (
    <HomeLayout {...baseOptions()} slots={{ container: HomeShell }}>
      {children}
    </HomeLayout>
  );
}
