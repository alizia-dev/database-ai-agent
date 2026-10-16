using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace DatabaseAIAgent.Database.Networking;

public enum HostRejection
{
    None = 0,
    InvalidHostName = 1,
    NotResolvable = 2,
    ReservedAddress = 3,
    PrivateNetwork = 4,
    Loopback = 5,
}

public sealed record HostCheck(HostRejection Rejection)
{
    public bool Allowed => Rejection == HostRejection.None;

    public string Message => Rejection switch
    {
        HostRejection.InvalidHostName => "Enter a host name or IP address only (no protocol, path, port or instance name).",
        HostRejection.NotResolvable => "The host name could not be resolved. Check the spelling and DNS.",
        HostRejection.ReservedAddress => "This address cannot be used as a database host.",
        HostRejection.PrivateNetwork => "Private network addresses are not allowed for this deployment. Use a public endpoint or ask your administrator about private connectivity.",
        HostRejection.Loopback => "Local addresses (localhost) are not allowed.",
        _ => string.Empty,
    };
}

/// <summary>
/// SSRF protection for customer-database hosts. The host must be a plain DNS name or IP literal, and every address it
/// resolves to must be allowed: loopback, link-local (cloud metadata endpoints), multicast and other reserved ranges
/// are always rejected (loopback only when explicitly allowed), private ranges unless the deployment allows them.
/// Checked when a connection is saved and again on every connection open (DNS can change).
/// </summary>
public sealed class HostAddressGuard(IOptions<DatabaseConnectionOptions> options)
{
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(5);

    public async Task<HostCheck> CheckAsync(string host, CancellationToken cancellationToken)
    {
        if (!IsSyntacticallyValid(host))
        {
            return new HostCheck(HostRejection.InvalidHostName);
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ResolveTimeout);
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, timeout.Token);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return new HostCheck(HostRejection.NotResolvable);
            }
        }

        if (addresses.Length == 0)
        {
            return new HostCheck(HostRejection.NotResolvable);
        }

        var settings = options.Value;
        foreach (var address in addresses)
        {
            var rejection = Classify(address, settings);
            if (rejection != HostRejection.None)
            {
                return new HostCheck(rejection);
            }
        }

        return new HostCheck(HostRejection.None);
    }

    internal static HostRejection Classify(IPAddress address, DatabaseConnectionOptions settings)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return settings.AllowLoopback ? HostRejection.None : HostRejection.Loopback;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 0 // "this network"
                || (b[0] == 169 && b[1] == 254) // link-local, incl. 169.254.169.254 cloud metadata
                || b[0] >= 224 // multicast, reserved, broadcast
                || (b[0] == 192 && b[1] == 0 && b[2] == 0) // IETF protocol assignments
                || (b[0] == 100 && b[1] == 100 && b[2] == 100 && b[3] == 200)) // Alibaba Cloud metadata
            {
                return HostRejection.ReservedAddress;
            }

            var isPrivate = b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) // carrier-grade NAT
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19)); // benchmarking
            return isPrivate && !settings.AllowPrivateNetworks ? HostRejection.PrivateNetwork : HostRejection.None;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any)
                || address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal
                || address.Equals(IPAddress.Parse("fd00:ec2::254"))) // AWS IMDS over IPv6
            {
                return HostRejection.ReservedAddress;
            }

            var isUniqueLocal = (address.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7
            return isUniqueLocal && !settings.AllowPrivateNetworks ? HostRejection.PrivateNetwork : HostRejection.None;
        }

        return HostRejection.ReservedAddress;
    }

    /// <summary>A DNS host name (RFC 1123 labels) or an IP literal; nothing that could smuggle connection-string syntax.</summary>
    internal static bool IsSyntacticallyValid(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253)
        {
            return false;
        }

        if (IPAddress.TryParse(host, out _))
        {
            return !host.Contains('%', StringComparison.Ordinal); // no IPv6 zone IDs
        }

        var labels = host.TrimEnd('.').Split('.');
        return labels.All(label =>
            label.Length is > 0 and <= 63
            && char.IsAsciiLetterOrDigit(label[0])
            && char.IsAsciiLetterOrDigit(label[^1])
            && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }
}
