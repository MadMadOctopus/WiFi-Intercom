using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IntercomCompanion.Core;

/// <summary>Immutable, signed application artifact. The private signing key is
/// never used by the companion or committed to this repository.</summary>
internal sealed record OtaPackage(string ManifestPath, string ImagePath, string Version,
    byte Protocol, long Size, string Sha256, string Signature)
{
    private const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEEisSIeNyz/nprL27lHmXjqS5GlJz
        vU0hygcTNvyz9seKIekc89pXbeVtZioyCGsF4+tM5/khTXjTsqhjljXsfQ==
        -----END PUBLIC KEY-----
        """;

    public static OtaPackage Load(string manifestPath)
    {
        var fullManifest = Path.GetFullPath(manifestPath);
        using var document = JsonDocument.Parse(File.ReadAllText(fullManifest));
        var root = document.RootElement;
        string RequiredString(string name) => root.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new InvalidDataException($"OTA manifest is missing {name}.");
        if (RequiredString("format") != "wifi-intercom-ota-1")
            throw new InvalidDataException("This is not a Wi-Fi Intercom OTA manifest.");
        var version = RequiredString("version");
        var imageName = RequiredString("image");
        var sha = RequiredString("sha256");
        var signature = RequiredString("signature");
        if (!root.TryGetProperty("protocol", out var protocolElement) || !protocolElement.TryGetByte(out var protocol) ||
            !root.TryGetProperty("size", out var sizeElement) || !sizeElement.TryGetInt64(out var size) || size <= 0)
            throw new InvalidDataException("OTA manifest has invalid protocol or size.");
        var imagePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fullManifest)!, imageName));
        if (!File.Exists(imagePath)) throw new FileNotFoundException("The OTA image named by the manifest was not found.", imagePath);
        var package = new OtaPackage(fullManifest, imagePath, version, protocol, size, sha, signature);
        package.Verify();
        return package;
    }

    public string CanonicalManifest() => $"wifi-intercom-ota-1\n{Version}\n{Protocol}\n{Size}\n{Sha256}\n";

    public void Verify()
    {
        if (Sha256.Length != 64 || Sha256.Any(ch => !char.IsAsciiHexDigit(ch)) ||
            !Sha256.All(ch => char.IsDigit(ch) || char.IsLower(ch)))
            throw new InvalidDataException("OTA hash must be lower-case SHA-256.");
        var file = new FileInfo(ImagePath);
        if (file.Length != Size) throw new InvalidDataException($"OTA image size mismatch ({file.Length} != {Size}).");
        using var stream = File.OpenRead(ImagePath);
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actualHash), Encoding.ASCII.GetBytes(Sha256)))
            throw new InvalidDataException("OTA image SHA-256 does not match its manifest.");
        using var key = ECDsa.Create();
        key.ImportFromPem(PublicKeyPem);
        // OpenSSL emits the ASN.1/DER ECDSA form and mbedTLS verifies that
        // same RFC 3279 representation on the device. .NET's default ECDsa
        // overload expects its fixed-width P1363 form, so select DER here.
        if (!key.VerifyData(Encoding.UTF8.GetBytes(CanonicalManifest()), Convert.FromBase64String(Signature),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            throw new InvalidDataException("OTA manifest signature is not trusted.");
    }
}
