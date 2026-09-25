using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Mainframe.Core;

public static class CertificateTrust
{
    public const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";
    public const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";

    public static string Fingerprint(X509Certificate2 certificate) =>
        certificate.GetCertHashString(HashAlgorithmName.SHA256);

    /// <summary>Validates purpose and dates against the cluster's explicit root, never the machine trust store.</summary>
    public static bool Validate(X509Certificate2 certificate, X509Certificate2 ca, string expectedEku)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(ca);
        if (expectedEku is not (ServerAuthentication or ClientAuthentication))
            throw new ArgumentException("Unsupported certificate purpose.", nameof(expectedEku));

        try
        {
            return ValidateCore(certificate, ca, expectedEku);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool ValidateCore(X509Certificate2 certificate, X509Certificate2 ca, string expectedEku)
    {
        // An explicit leaf EKU is required: absence must not implicitly grant every role.
        var usages = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().ToArray();
        if (usages.Length != 1 || !usages[0].EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == expectedEku))
            return false;
        if (certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority))
            return false;

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(expectedEku));
        return chain.Build(certificate)
            && chain.ChainElements.Count == 2
            && string.Equals(Fingerprint(chain.ChainElements[^1].Certificate), Fingerprint(ca), StringComparison.Ordinal);
    }
}
