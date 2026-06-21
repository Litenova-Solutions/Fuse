import { cn } from '@/lib/cn';

/**
 * The Fuse brand mark: several source lines on the left converging through a
 * single node into one reduced output line on the right. Uses currentColor so
 * it inherits text color in light and dark themes.
 */
export function FuseMark({ className }: { className?: string }) {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      className={cn('text-[var(--color-fd-primary)]', className)}
      aria-hidden
    >
      {/* incoming source lines */}
      <path d="M2 5h6" opacity="0.55" />
      <path d="M2 12h5" opacity="0.8" />
      <path d="M2 19h6" opacity="0.55" />
      {/* convergence into the node */}
      <path d="M8 5c3 0 1.5 7 4 7" opacity="0.55" />
      <path d="M8 19c3 0 1.5-7 4-7" opacity="0.55" />
      {/* the fused node and single output */}
      <circle cx="13" cy="12" r="2.2" fill="currentColor" stroke="none" />
      <path d="M15 12h7" />
    </svg>
  );
}
