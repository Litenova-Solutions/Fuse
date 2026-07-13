namespace OrderingApp.Ordering;

// A typed HttpClient service (G2 iteration 2: AddHttpClient<IPricingClient, PricingClient>()). The typed-client
// registration resolves IPricingClient to PricingClient exactly as a plain DI registration would, so the analyzer
// emits the same di_resolves_to edge.
public interface IPricingClient
{
    long QuoteTicks();
}

public sealed class PricingClient : IPricingClient
{
    public long QuoteTicks() => 0;
}
