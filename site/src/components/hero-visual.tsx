'use client';

import { motion, useInView, useReducedMotion } from 'motion/react';
import { useEffect, useRef, useState } from 'react';

const RAW = 1_487_000;
const FUSED = 880_000;

function useCountUp(target: number, run: boolean, durationMs = 1100) {
  const [value, setValue] = useState(run ? 0 : target);
  const reduce = useReducedMotion();

  useEffect(() => {
    if (!run) return;
    if (reduce) {
      setValue(target);
      return;
    }
    let raf = 0;
    let start: number | null = null;
    const step = (t: number) => {
      if (start === null) start = t;
      const p = Math.min(1, (t - start) / durationMs);
      // easeOutCubic
      const eased = 1 - Math.pow(1 - p, 3);
      setValue(Math.round(target * eased));
      if (p < 1) raf = requestAnimationFrame(step);
    };
    raf = requestAnimationFrame(step);
    return () => cancelAnimationFrame(raf);
  }, [run, target, durationMs, reduce]);

  return value;
}

/**
 * The single Motion moment on the landing page: a terminal card whose token
 * bar collapses from the raw repository count to the fused count, with the
 * number counting down alongside it. Numbers are the measured Newtonsoft.Json
 * `--all` arm (see the benchmarks page). Respects reduced-motion.
 */
export function HeroVisual() {
  const ref = useRef<HTMLDivElement>(null);
  const inView = useInView(ref, { once: true, amount: 0.4 });
  const reduce = useReducedMotion();
  const fusedCount = useCountUp(FUSED, inView);
  const pct = Math.round((1 - FUSED / RAW) * 100);

  const lines = [
    { text: '$ fuse dotnet --directory ./src --all', muted: false },
    { text: 'Collecting 945 C# files ...', muted: true },
    { text: 'Reducing ... cache: 0 hit / 945 miss', muted: true },
  ];

  return (
    <div ref={ref} className="relative w-full max-w-xl">
      <div className="pointer-events-none absolute -inset-x-8 -top-10 h-40 brand-glow" />
      <div className="relative rounded-xl border border-fd-border bg-fd-card/80 shadow-xl backdrop-blur">
        <div className="flex items-center gap-1.5 border-b border-fd-border px-4 py-3">
          <span className="size-3 rounded-full bg-red-400/70" />
          <span className="size-3 rounded-full bg-yellow-400/70" />
          <span className="size-3 rounded-full bg-green-400/70" />
          <span className="ml-3 text-xs text-fd-muted-foreground">fuse</span>
        </div>

        <div className="space-y-1.5 px-4 py-4 font-mono text-[13px] leading-relaxed">
          {lines.map((line, i) => (
            <motion.div
              key={line.text}
              initial={reduce ? false : { opacity: 0, y: 4 }}
              animate={inView ? { opacity: 1, y: 0 } : undefined}
              transition={{ delay: 0.1 + i * 0.18, duration: 0.3 }}
              className={line.muted ? 'text-fd-muted-foreground' : 'text-fd-foreground'}
            >
              {line.text}
            </motion.div>
          ))}

          <motion.div
            initial={reduce ? false : { opacity: 0 }}
            animate={inView ? { opacity: 1 } : undefined}
            transition={{ delay: 0.7, duration: 0.3 }}
            className="pt-2"
          >
            <div className="flex items-baseline justify-between text-xs text-fd-muted-foreground">
              <span>raw concatenation</span>
              <span className="tabular-nums">{RAW.toLocaleString()} tokens</span>
            </div>
            <div className="mt-1 h-2 w-full overflow-hidden rounded-full bg-fd-muted">
              <div className="h-full w-full rounded-full bg-fd-muted-foreground/30" />
            </div>

            <div className="mt-3 flex items-baseline justify-between text-xs">
              <span className="font-medium text-fd-foreground">fused output</span>
              <span className="tabular-nums font-medium text-[var(--brand)]">
                {fusedCount.toLocaleString()} tokens
              </span>
            </div>
            <div className="mt-1 h-2 w-full overflow-hidden rounded-full bg-fd-muted">
              <motion.div
                initial={reduce ? false : { width: '100%' }}
                animate={inView ? { width: `${(FUSED / RAW) * 100}%` } : undefined}
                transition={{ delay: 0.8, duration: 1.1, ease: 'easeOut' }}
                className="h-full rounded-full"
                style={{
                  background: 'linear-gradient(90deg, var(--brand), var(--brand-soft))',
                }}
              />
            </div>
          </motion.div>

          <motion.div
            initial={reduce ? false : { opacity: 0, y: 4 }}
            animate={inView ? { opacity: 1, y: 0 } : undefined}
            transition={{ delay: 1.5, duration: 0.3 }}
            className="pt-2 text-fd-foreground"
          >
            <span className="text-[var(--brand)]">{pct}% fewer tokens</span>{' '}
            <span className="text-fd-muted-foreground">
              at 100% of public types and methods
            </span>
          </motion.div>
        </div>
      </div>
      <p className="mt-3 text-center text-xs text-fd-muted-foreground">
        Measured: Newtonsoft.Json, <code className="font-mono">--all</code> mode.{' '}
        <a href="/docs/project/benchmarks" className="underline hover:text-fd-foreground">
          See the benchmarks
        </a>
        .
      </p>
    </div>
  );
}
