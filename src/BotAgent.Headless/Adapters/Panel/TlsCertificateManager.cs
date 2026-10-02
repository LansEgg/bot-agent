using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BotAgent.Services;

namespace BotAgent.Adapters.Panel;

/// <summary>
/// TLS / SSL 证书管理服务：
/// 支持自签名 X.509 证书的自动生成与导出（无需 OpenSSL 二进制依赖），
/// 以及 Let's Encrypt 标准证书链（fullchain.pem / privkey.pem）或 PKCS#12 (.pfx) 的读取与加载。
/// </summary>
public static class TlsCertificateManager
{
    private static readonly object Gate = new();

    public static string CertsDirectory => Path.Combine(AppPaths.RuntimeRoot, "certs");
    public static string DefaultCrtPath => Path.Combine(CertsDirectory, "botagent.crt");
    public static string DefaultKeyPath => Path.Combine(CertsDirectory, "botagent.key");
    public static string DefaultPfxPath => Path.Combine(CertsDirectory, "botagent.pfx");

    /// <summary>
    /// 尝试加载或生成可用的 X509Certificate2 证书。
    /// 优先级：环境变量指定的证书文件 -> runtime/certs/botagent.pfx -> 自动生成自签证书。
    /// </summary>
    public static X509Certificate2? EnsureCertificate(bool autoGenerateIfMissing = true)
    {
        lock (Gate)
        {
            // 1. 检查环境变量自定义指定（如 Let's Encrypt 产物路径）
            var customPfx = Environment.GetEnvironmentVariable("QQCHAT_TLS_PFX");
            if (!string.IsNullOrWhiteSpace(customPfx) && File.Exists(customPfx))
            {
                var pass = Environment.GetEnvironmentVariable("QQCHAT_TLS_PASSWORD") ?? string.Empty;
                return new X509Certificate2(customPfx, pass);
            }

            var customCert = Environment.GetEnvironmentVariable("QQCHAT_TLS_CERT");
            var customKey = Environment.GetEnvironmentVariable("QQCHAT_TLS_KEY");
            if (!string.IsNullOrWhiteSpace(customCert) && !string.IsNullOrWhiteSpace(customKey) &&
                File.Exists(customCert) && File.Exists(customKey))
            {
                return X509Certificate2.CreateFromPemFile(customCert, customKey);
            }

            // 2. 检查默认 certs 目录
            Directory.CreateDirectory(CertsDirectory);
            if (File.Exists(DefaultPfxPath))
            {
                return new X509Certificate2(DefaultPfxPath);
            }

            if (File.Exists(DefaultCrtPath) && File.Exists(DefaultKeyPath))
            {
                return X509Certificate2.CreateFromPemFile(DefaultCrtPath, DefaultKeyPath);
            }

            // 3. 自动生成自签名 X.509 证书（有效 3 年，SAN 包含 localhost, 127.0.0.1）
            if (autoGenerateIfMissing)
            {
                return GenerateSelfSignedCertificate("BotAgent", "localhost");
            }

            return null;
        }
    }

    /// <summary>
    /// 生成符合标准 RFC 5280 规范的自签名 TLS 证书并存盘。
    /// </summary>
    public static X509Certificate2 GenerateSelfSignedCertificate(string commonName, params string[] subjectAltNames)
    {
        Directory.CreateDirectory(CertsDirectory);

        using var rsa = RSA.Create(2048);
        var subject = new X500DistinguishedName($"CN={commonName}, O=BotAgent");
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false)); // Server Authentication

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);

        foreach (var san in subjectAltNames)
        {
            if (string.IsNullOrWhiteSpace(san)) continue;
            if (IPAddress.TryParse(san.Trim(), out var ip))
            {
                sanBuilder.AddIpAddress(ip);
            }
            else
            {
                sanBuilder.AddDnsName(san.Trim());
            }
        }
        request.CertificateExtensions.Add(sanBuilder.Build());

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddYears(3);

        using var cert = request.CreateSelfSigned(notBefore, notAfter);

        // 导出 PEM 格式证书与私钥（方便 Nginx / Caddy / ACME 兼容）
        var certPem = cert.ExportCertificatePem();
        var keyPem = rsa.ExportPkcs8PrivateKeyPem();
        File.WriteAllText(DefaultCrtPath, certPem);
        File.WriteAllText(DefaultKeyPath, keyPem);

        // 导出 PFX (PKCS#12) 格式（供 .NET 运行时直接加载）
        var pfxBytes = cert.Export(X509ContentType.Pfx);
        File.WriteAllBytes(DefaultPfxPath, pfxBytes);

        FileLog.Write("TLS", $"已生成自签名 SSL/TLS 证书：{DefaultCrtPath}（有效期至 {notAfter:yyyy-MM-dd}）");
        return new X509Certificate2(DefaultPfxPath);
    }
}
