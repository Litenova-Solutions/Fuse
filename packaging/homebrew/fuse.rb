# Homebrew formula for Fuse (Linux x64).
#
# macOS is not yet supported: there is no macOS AOT build, so this formula
# defines only a Linux bottle source. Update `version` and the `sha256` (from
# the release SHA256SUMS.txt) on each release, or let the tap's bump workflow
# do it.
class Fuse < Formula
  desc "A .NET-native codebase context optimizer for AI-assisted development"
  homepage "https://fuse.codes"
  version "2.0.0"
  license "MIT"

  on_linux do
    url "https://github.com/Litenova-Solutions/Fuse/releases/download/v2.0.0/fuse-2.0.0-linux-x64.tar.gz"
    sha256 "0000000000000000000000000000000000000000000000000000000000000000"
  end

  def install
    bin.install "fuse"
  end

  test do
    assert_match "fuse", shell_output("#{bin}/fuse --help")
  end
end
