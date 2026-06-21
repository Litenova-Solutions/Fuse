import * as React from 'react';
import { Slot } from '@radix-ui/react-slot';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from '@/lib/cn';

const buttonVariants = cva(
  'inline-flex items-center justify-center gap-2 rounded-lg text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--color-fd-ring)] disabled:pointer-events-none disabled:opacity-50 whitespace-nowrap',
  {
    variants: {
      variant: {
        primary:
          'bg-[var(--color-fd-primary)] text-[var(--color-fd-primary-foreground)] hover:opacity-90 shadow-sm',
        secondary:
          'border border-[var(--color-fd-border)] bg-[var(--color-fd-card)] text-[var(--color-fd-foreground)] hover:bg-[var(--color-fd-accent)]',
        ghost:
          'text-[var(--color-fd-muted-foreground)] hover:bg-[var(--color-fd-accent)] hover:text-[var(--color-fd-accent-foreground)]',
      },
      size: {
        default: 'h-10 px-5',
        lg: 'h-12 px-7 text-base',
        sm: 'h-9 px-4',
      },
    },
    defaultVariants: {
      variant: 'primary',
      size: 'default',
    },
  },
);

export interface ButtonProps
  extends React.ButtonHTMLAttributes<HTMLButtonElement>,
    VariantProps<typeof buttonVariants> {
  asChild?: boolean;
}

/** A button styled with the Fumadocs theme tokens, with a `primary`, `secondary`, and `ghost` variant. */
export const Button = React.forwardRef<HTMLButtonElement, ButtonProps>(
  ({ className, variant, size, asChild = false, ...props }, ref) => {
    const Comp = asChild ? Slot : 'button';
    return (
      <Comp
        ref={ref}
        className={cn(buttonVariants({ variant, size }), className)}
        {...props}
      />
    );
  },
);
Button.displayName = 'Button';

export { buttonVariants };
