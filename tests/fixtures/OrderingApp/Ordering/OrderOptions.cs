namespace OrderingApp.Ordering;

// Bound to the "Orders" configuration section in Program.ConfigureServices.
public sealed class OrderOptions
{
    public int MaxItems { get; set; }

    public string Currency { get; set; } = "USD";
}
