#nullable enable

using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Impunity.Tests
{
	// Private, per-fixture trust. Never installs a root in the machine store or disables certificate validation.
	public sealed class TestCertificates : IDisposable
	{
		public string DirectoryPath { get; } = Path.Combine(TestEnv.TempRoot, "ImpunityTls_" + Guid.NewGuid().ToString("N"));
		public string CertificatePath => Path.Combine(DirectoryPath, "fullchain.pem");
		public string PrivateKeyPath => Path.Combine(DirectoryPath, "privkey.pem");
		public X509Certificate2 Root { get; }
		private readonly X509Certificate2 Issuer;
		public string Thumbprint { get; private set; } = "";

		public TestCertificates()
		{
			Directory.CreateDirectory(DirectoryPath);
			using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
			var rootRequest = new CertificateRequest("CN=Impunity test root " + Guid.NewGuid(), rootKey, HashAlgorithmName.SHA256);
			AddCAExtensions(rootRequest);
			Root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
			using var issuerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
			var issuerRequest = new CertificateRequest("CN=Impunity test intermediate", issuerKey, HashAlgorithmName.SHA256);
			AddCAExtensions(issuerRequest);
			using var issuer = issuerRequest.Create(Root, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(20), RandomNumberGenerator.GetBytes(16));
			Issuer = issuer.CopyWithPrivateKey(issuerKey);
			Rotate();
		}

		private static void AddCAExtensions(CertificateRequest request)
		{
			request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
			request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
			request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
		}

		public (string certificate, string key) Issue(bool rsa = false, bool expired = false, bool clientOnly = false)
		{
			using AsymmetricAlgorithm key = rsa ? RSA.Create(2048) : ECDsa.Create(ECCurve.NamedCurves.nistP256);
			var request = key is RSA rsaKey
				? new CertificateRequest("CN=localhost", rsaKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
				: new CertificateRequest("CN=localhost", (ECDsa)key, HashAlgorithmName.SHA256);
			var names = new SubjectAlternativeNameBuilder();
			names.AddDnsName("localhost");
			names.AddIpAddress(IPAddress.Loopback);
			names.AddIpAddress(IPAddress.IPv6Loopback);
			request.CertificateExtensions.Add(names.Build());
			request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
			request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
			request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
			{
				new Oid(clientOnly ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1")
			}, true));
			using var signer = Issuer.GetECDsaPrivateKey()!;
			using var leaf = request.Create(Issuer.SubjectName, X509SignatureGenerator.CreateForECDsa(signer),
				DateTimeOffset.UtcNow.AddMinutes(-30), expired ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddDays(7),
				RandomNumberGenerator.GetBytes(16));
			return (leaf.ExportCertificatePem() + "\n" + Issuer.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
		}

		public void Rotate(bool rsa = false)
		{
			var files = Issue(rsa);
			File.WriteAllText(CertificatePath, files.certificate);
			File.WriteAllText(PrivateKeyPath, files.key);
			using var leaf = X509Certificate2.CreateFromPem(files.certificate);
			Thumbprint = leaf.Thumbprint;
		}

		public bool Validate(object sender, X509Certificate? certificate, X509Chain? suppliedChain, SslPolicyErrors errors)
		{
			if (certificate == null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0) return false;
			using var chain = new X509Chain();
			chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
			chain.ChainPolicy.CustomTrustStore.Add(Root);
			chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
			chain.ChainPolicy.DisableCertificateDownloads = true;
			chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
			// Only intermediates actually supplied by the server, never our Issuer field. This detects a server
			// accidentally sending just the leaf. Test issuers have no AIA URL or machine-store installation.
			if (suppliedChain != null)
				foreach (var element in suppliedChain.ChainElements) chain.ChainPolicy.ExtraStore.Add(element.Certificate);
			using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
			return chain.Build(leaf);
		}

		public void Dispose()
		{
			Issuer.Dispose();
			Root.Dispose();
			Directory.Delete(DirectoryPath, recursive: true);
		}
	}
}
