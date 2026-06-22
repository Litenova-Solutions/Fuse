'use client';

import { motion, useInView, useReducedMotion } from 'motion/react';
import { useRef } from 'react';

/**
 * The single Motion moment on the landing page: a terminal card that contrasts
 * an agent's blind explore phase (file-open after file-open) with one scoped
 * Fuse call. The round-trip figure is a structural lower bound and the token
 * figure is paired with its recall, matching the benchmarks page (Layer 4).
 * Respects reduced-motion.
 */
export function HeroVisual() {
  const ref = useRef<HTMLDivElement>(null);
  const inView = useInView(ref, { once: true, amount: 0.4 });
  const reduce = useReducedMotion();

  const blindReads = [
    'read OrderService.cs',
    'read Order.cs',
    'read PricingPolicy.cs',
    'grep "discount" ... read 3 more',
  ];

  return (
    <div ref={ref} className="relative w-full max-w-xl">
      <div className="pointer-events-none absolute -inset-x-8 -top-10 h-40 brand-glow" />
      <div className="relative rounded-xl border border-fd-border bg-fd-card/80 shadow-xl backdrop-blur">
        <div className="flex items-center gap-1.5 border-b border-fd-border px-4 py-3">
          <span className="size-3 rounded-full bg-red-400/70" />
          <span className="size-3 rounded-full bg-yellow-400/70" />
          <span className="size-3 rounded-full bg-green-400/70" />
          <span className="ml-3 text-xs text-fd-muted-foreground">agent + fuse</span>
        </div>

        <div className="space-y-1.5 px-4 py-4 font-mono text-[13px] leading-relaxed">
          <div className="text-fd-muted-foreground">
            # task: apply the discount at checkout
          </div>

          {/* Without Fuse: blind reads, one round-trip each */}
          {blindReads.map((line, i) => (
            <motion.div
              key={line}
              initial={reduce ? false : { opacity: 0, y: 4 }}
              animate={inView ? { opacity: 1, y: 0 } : undefined}
              transition={{ delay: 0.1 + i * 0.16, duration: 0.3 }}
              className="text-fd-muted-foreground/80"
            >
              <span className="text-fd-muted-foreground/50">without fuse </span>
              {line}
            </motion.div>
          ))}

          <motion.div
            initial={reduce ? false : { opacity: 0 }}
            animate={inView ? { opacity: 1 } : undefined}
            transition={{ delay: 0.85, duration: 0.3 }}
            className="flex items-baseline justify-between pt-1 text-xs text-fd-muted-foreground"
          >
            <span>blind explore phase</span>
            <span className="tabular-nums">&gt;= 6 round-trips</span>
          </motion.div>
          <div className="h-2 w-full overflow-hidden rounded-full bg-fd-muted">
            <div className="h-full w-full rounded-full bg-fd-muted-foreground/30" />
          </div>

          {/* With Fuse: one scoped call */}
          <motion.div
            initial={reduce ? false : { opacity: 0, y: 4 }}
            animate={inView ? { opacity: 1, y: 0 } : undefined}
            transition={{ delay: 1.1, duration: 0.3 }}
            className="pt-3 text-fd-foreground"
          >
            <span className="text-[var(--brand)]">fuse_ask</span>
            (&quot;discount at checkout&quot;, 20k)
          </motion.div>

          <motion.div
            initial={reduce ? false : { opacity: 0 }}
            animate={inView ? { opacity: 1 } : undefined}
            transition={{ delay: 1.3, duration: 0.3 }}
            className="flex items-baseline justify-between pt-1 text-xs"
          >
            <span className="font-medium text-fd-foreground">one scoped call</span>
            <span className="tabular-nums font-medium text-[var(--brand)]">1 call</span>
          </motion.div>
          <div className="h-2 w-full overflow-hidden rounded-full bg-fd-muted">
            <motion.div
              initial={reduce ? false : { width: '100%' }}
              animate={inView ? { width: '16%' } : undefined}
              transition={{ delay: 1.4, duration: 1.0, ease: 'easeOut' }}
              className="h-full rounded-full"
              style={{
                background: 'linear-gradient(90deg, var(--brand), var(--brand-soft))',
              }}
            />
          </div>

          <motion.div
            initial={reduce ? false : { opacity: 0, y: 4 }}
            animate={inView ? { opacity: 1, y: 0 } : undefined}
            transition={{ delay: 1.9, duration: 0.3 }}
            className="pt-2 text-fd-foreground"
          >
            <span className="text-[var(--brand)]">~40K tokens</span>{' '}
            <span className="text-fd-muted-foreground">
              at 51% recall, vs ~512K for a packer dump
            </span>
          </motion.div>
        </div>
      </div>
      <p className="mt-3 text-center text-xs text-fd-muted-foreground">
        Lower-bound round-trips over 24 real merged PRs.{' '}
        <a href="/docs/project/benchmarks" className="underline hover:text-fd-foreground">
          See the benchmarks
        </a>
        .
      </p>
    </div>
  );
}
