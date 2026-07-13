'use client';

import Link from 'next/link';
import { useState } from 'react';

const installCommand = 'dotnet tool install -g Fuse';
const connectCommand = 'fuse mcp install --rules';
const allCommands = `${installCommand}\n${connectCommand}`;

export function HeroInstallCommands() {
  const [copied, setCopied] = useState(false);

  async function copyCommands() {
    try {
      await navigator.clipboard.writeText(allCommands);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2000);
    } catch {
      // Clipboard may be unavailable; ignore.
    }
  }

  return (
    <div className="home-install mx-auto mt-10 max-w-2xl">
      <div className="home-install__chrome">
        <div className="home-install__dots" aria-hidden="true">
          <span />
          <span />
          <span />
        </div>
        <span className="home-install__title">shell</span>
        <button
          type="button"
          className="home-install__copy"
          onClick={copyCommands}
          aria-label="Copy install commands"
        >
          {copied ? 'Copied' : 'Copy'}
        </button>
      </div>
      <pre className="home-install__body">
        <code>
          <span className="home-install__comment"># Install the .NET global tool</span>
          {'\n'}
          <span className="home-install__prompt">$ </span>
          {installCommand}
          {'\n\n'}
          <span className="home-install__comment"># Connect to your coding agent</span>
          {'\n'}
          <span className="home-install__prompt">$ </span>
          {connectCommand}
        </code>
      </pre>
      <p className="home-install__footer">
        <Link href="/docs/start/install">WinGet, install scripts, and other options</Link>
      </p>
    </div>
  );
}
