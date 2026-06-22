using System.Net.Http.Json;
using System.Text.Json;
using Fuse.Emission.Tokenization;

namespace Fuse.Emission.Tests;

// Calibration of the Anthropic and Gemini approximate counters (roadmap 4.1). The committed constants are the
// providers' published rules-of-thumb; calibration to a measured band is pending a working provider key (see
// TokenizerFactory and tests/benchmarks/harness/calibrate-tokenizers.ps1). The regression guards below pin the
// committed constants; the live-API test is gated on a usable key and skips otherwise.
public sealed class TokenizerCalibrationTests
{
    private const string AnthropicApiKeyVariable = "ANTHROPIC_API_KEY";

    // A single 35-character alphanumeric run isolates the per-word-run constant from punctuation handling:
    // the counter charges ceil(35 / charsPerToken) for the run. ceil(35/3.5)=10 pins Anthropic at 3.5;
    // ceil(35/4.0)=9 pins Gemini at 4.0. A change to either constant breaks the matching assertion.
    private const string Word35Exact = "abcdefghijklmnopqrstuvwxyz012345678"; // 35 chars

    [Fact]
    public void AnthropicCounter_PinsCommittedCharsPerToken()
    {
        var counter = new TokenizerFactory().GetCounter("claude-opus-4-8");
        Assert.Equal(10, counter.Count(Word35Exact)); // ceil(35 / 3.5)
    }

    [Fact]
    public void GeminiCounter_PinsCommittedCharsPerToken()
    {
        var counter = new TokenizerFactory().GetCounter("gemini-1.5-pro");
        Assert.Equal(9, counter.Count(Word35Exact)); // ceil(35 / 4.0)
    }

    [Fact]
    public void GeminiCounter_PredictsFewerTokensThanAnthropic_ForCode()
    {
        var factory = new TokenizerFactory();
        const string sample = "public sealed class OrderProcessor { public decimal ComputeTotal(Order order) => order.Lines.Sum(l => l.Price); }";

        var anthropic = factory.GetCounter("claude-opus-4-8").Count(sample);
        var gemini = factory.GetCounter("gemini-1.5-pro").Count(sample);

        // Gemini's larger chars-per-token must not predict more tokens than Anthropic's for the same code.
        Assert.True(gemini <= anthropic, $"gemini={gemini} anthropic={anthropic}");
    }

    [Fact]
    public async Task AnthropicEstimate_IsWithinSanityBand_OfGroundTruth_WhenKeyAvailable()
    {
        var apiKey = Environment.GetEnvironmentVariable(AnthropicApiKeyVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
            return; // No key: the live-API portion is skipped (CI without a provider key).

        const string sample = """
            using System;
            namespace Sample;
            public sealed class InvoiceCalculator
            {
                public decimal ComputeGrandTotal(Invoice invoice, decimal taxRate)
                {
                    var subtotal = invoice.LineItems.Sum(item => item.UnitPrice * item.Quantity);
                    var tax = Math.Round(subtotal * taxRate, 2, MidpointRounding.AwayFromZero);
                    return subtotal + tax - invoice.Discounts.Sum(d => d.Amount);
                }
            }
            """;

        int actual;
        try
        {
            actual = await CountAnthropicTokensAsync(apiKey, sample);
        }
        catch
        {
            return; // Key invalid, offline, or transport error: skip rather than fail the suite.
        }

        var predicted = new TokenizerFactory().GetCounter("claude-opus-4-8").Count(sample);
        var error = Math.Abs(predicted - actual) / (double)actual;

        // A loose sanity band only: the published rule-of-thumb is uncalibrated, so this asserts the estimate
        // is in the right ballpark, not the tight band a fitted constant would reach. The exact ratio is left
        // in the failure message so a maintainer with a working key can tighten the constant (see 4.1 TODO).
        Assert.True(error < 0.5, $"predicted={predicted} actual={actual} relErr={error:P0}");
    }

    private static async Task<int> CountAnthropicTokensAsync(string apiKey, string content)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages/count_tokens");
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = JsonContent.Create(new
        {
            model = "claude-opus-4-8",
            messages = new[] { new { role = "user", content } },
        });

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.GetProperty("input_tokens").GetInt32();
    }
}
