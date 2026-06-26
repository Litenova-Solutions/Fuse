# Pester tests for the harness helpers whose logic is not trivially obvious.
# Run from the repo root with the bundled Pester:
#   pwsh -NoProfile -Command "Invoke-Pester -Path tests/benchmarks/harness/common.Tests.ps1"
# Note: if TEMP resolves to an 8.3 short path, Pester 3.x TestDrive teardown can throw a spurious
# PSArgumentException after the assertions pass; set TEMP/TMP to a long path first to avoid it.
# These cover Test-PrTitleRelevant, the title/diff-mismatch filter that gen-prs.ps1 uses to drop
# merged PRs whose title is a misleading maintenance description over a real C# diff (a CI tweak or a
# dependency/version bump), since a title-keyword scope cannot locate such a change set. The filter must
# keep genuine code-change titles (including terse ones) and merge-noise titles (the layers recover those
# with a type-name fallback), and reject only the misleading-maintenance class.

. "$PSScriptRoot/common.ps1"

Describe "Test-PrTitleRelevant" {

    Context "rejects misleading maintenance titles (title/diff mismatch)" {
        It "rejects a CI-prefixed title (AutoMapper#4634)" {
            Test-PrTitleRelevant 'ci: skip Azure login and signing on PRs (publish-only, secrets absent)' | Should Be $false
        }
        It "rejects a version-bump 'from X to Y' title (AutoMapper#4616)" {
            Test-PrTitleRelevant 'Update Microsoft.Sbom.DotNetTool from 1.2.0 to 4.1.5' | Should Be $false
        }
        It "rejects a dependabot Bump title (eShopOnWeb#835)" {
            Test-PrTitleRelevant 'Bump Moq from 4.18.3 to 4.18.4' | Should Be $false
        }
        It "rejects a build(deps) conventional-commit prefix" {
            Test-PrTitleRelevant 'build(deps): bump xunit from 2.7.0 to 2.8.0' | Should Be $false
        }
        It "rejects a chore: prefix" {
            Test-PrTitleRelevant 'chore: update CI matrix' | Should Be $false
        }
        It "rejects an empty title" {
            Test-PrTitleRelevant '' | Should Be $false
        }
    }

    Context "keeps genuine code-change titles" {
        It "keeps a feature title" {
            Test-PrTitleRelevant 'Allow to remove items from the basket setting quantity to zero' | Should Be $true
        }
        It "keeps a terse fix title" {
            Test-PrTitleRelevant 'fixed connection strings' | Should Be $true
        }
        It "keeps a refactor title that mentions a service" {
            Test-PrTitleRelevant 'BasketService no longer uses UriComposer' | Should Be $true
        }
        It "keeps a title that merely contains the word from without a version" {
            Test-PrTitleRelevant 'Remove items from the basket' | Should Be $true
        }
        It "keeps a merge-noise title (the layers recover these with a type-name fallback)" {
            Test-PrTitleRelevant "Merge branch 'master' into timeout-behavior-support" | Should Be $true
        }
    }
}
