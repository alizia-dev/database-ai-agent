using System.Globalization;

namespace DatabaseAIAgent.Billing.Providers;

/// <summary>Converts between provider minor units (cents) and decimal amounts.</summary>
internal static class Money
{
    // ISO 4217 currencies without a minor unit, per Stripe's documentation.
    private static readonly HashSet<string> ZeroDecimalCurrencies = new(StringComparer.OrdinalIgnoreCase)
    {
        "bif", "clp", "djf", "gnf", "jpy", "kmf", "krw", "mga", "pyg", "rwf", "ugx", "vnd", "vuv", "xaf", "xof", "xpf",
    };

    public static decimal FromMinorUnits(long amount, string currency) =>
        ZeroDecimalCurrencies.Contains(currency) ? amount : amount / 100m;

    public static long ToMinorUnits(decimal amount, string currency) =>
        (long)Math.Round(ZeroDecimalCurrencies.Contains(currency) ? amount : amount * 100m, MidpointRounding.AwayFromZero);

    public static decimal ParseDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) ? amount : 0m;

    public static string Format(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
}
