#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Impunity.StandaloneServer;

/// <summary>Loads Certbot PEM files and publishes complete TLS certificate generations without restarting Kestrel.</summary>
public sealed class HttpsCertificateService : IAsyncDisposable
{
	private sealed class Generation : IDisposable
	{
		public required X509Certificate2 Leaf;
		public required X509Certificate2Collection Chain;
		public required SslStreamCertificateContext Context;
		public required string Hash;
		public void Dispose()
		{
			Leaf.Dispose();
			foreach (var certificate in Chain) certificate.Dispose();
		}
	}

	private readonly string CertificatePath;
	private readonly string KeyPath;
	private readonly TimeSpan Interval;
	private readonly ILogger Logger;
	private readonly CancellationTokenSource Stop = new();
	// A handshake or an established SslStream may still reference an old generation. Renewals are infrequent;
	// keep successful generations until Kestrel has stopped instead of racing certificate disposal with it.
	private readonly List<Generation> Generations = new();
	private Generation Current;
	private Task? PollTask;
	private DateTimeOffset NextFailureLog;
	private DateTimeOffset NextExpiryLog;

	/// <summary>Loads and validates the initial certificate; failure prevents the server from starting.</summary>
	public HttpsCertificateService(HttpsConfig config, string configPath, ILogger logger)
	{
		string directory = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
		CertificatePath = Path.GetFullPath(config.certificate_path!, directory);
		KeyPath = Path.GetFullPath(config.private_key_path!, directory);
		Interval = TimeSpan.FromSeconds(config.reload_interval_seconds);
		Logger = logger;
		Current = Load(null)!;
		Generations.Add(Current);
		LogLoaded(Current);
	}

	/// <summary>Returns a fresh options object with the current immutable certificate context for a TLS handshake.</summary>
	public SslServerAuthenticationOptions CreateAuthenticationOptions() => new()
	{
		ServerCertificateContext = Volatile.Read(ref Current).Context
	};

	/// <summary>Starts periodic checks. The owner must dispose this service after stopping the web host.</summary>
	public void Start() => PollTask ??= PollAsync();

	private async Task PollAsync()
	{
		using var timer = new PeriodicTimer(Interval);
		try
		{
			while (await timer.WaitForNextTickAsync(Stop.Token))
			{
				try
				{
					var candidate = Load(Current.Hash);
					if (candidate != null)
					{
						Generations.Add(candidate);
						Volatile.Write(ref Current, candidate);
						NextFailureLog = NextExpiryLog = default;
						LogLoaded(candidate);
					}
				}
				catch (Exception e)
				{
					if (DateTimeOffset.UtcNow >= NextFailureLog)
					{
						Logger.LogWarning("HTTPS certificate reload failed; retaining {Fingerprint}, expires {Expiry:u}: {Reason}",
							Current.Leaf.Thumbprint, Current.Leaf.NotAfter.ToUniversalTime(), e.Message);
						NextFailureLog = DateTimeOffset.UtcNow.AddMinutes(5);
					}
				}
				if (Current.Leaf.NotAfter.ToUniversalTime() < DateTime.UtcNow.AddDays(14) && DateTimeOffset.UtcNow >= NextExpiryLog)
				{
					Logger.LogWarning("Active HTTPS certificate expires {Expiry:u}; check Certbot renewal and file permissions.",
						Current.Leaf.NotAfter.ToUniversalTime());
					NextExpiryLog = DateTimeOffset.UtcNow.AddDays(1);
				}
			}
		}
		catch (OperationCanceledException) when (Stop.IsCancellationRequested) { }
	}

	private Generation? Load(string? previousHash)
	{
		// Reopen paths on every poll: Certbot replaces live/ symlinks and may preserve timestamps. Do not resolve
		// and cache their archive targets. Read again before publication to catch files moving during renewal.
		string pem = File.ReadAllText(CertificatePath);
		string key = File.ReadAllText(KeyPath);
		string hash = Hash(pem) + Hash(key);
		if (hash == previousHash) return null;
		X509Certificate2? leaf = null;
		var chain = new X509Certificate2Collection();
		try
		{
			leaf = X509Certificate2.CreateFromPem(pem, key);
			if (!leaf.HasPrivateKey || leaf.NotBefore.ToUniversalTime() > DateTime.UtcNow || leaf.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
				throw new InvalidDataException("Certificate must have a matching private key and be currently valid.");
			foreach (var extension in leaf.Extensions)
			{
				if (extension is X509EnhancedKeyUsageExtension usage)
				{
					bool serverAuth = false;
					foreach (Oid oid in usage.EnhancedKeyUsages)
						serverAuth |= oid.Value == "1.3.6.1.5.5.7.3.1" || oid.Value == "2.5.29.37.0";
					if (!serverAuth) throw new InvalidDataException("Certificate is not valid for TLS server authentication.");
				}
			}
			chain.ImportFromPem(pem);
			// CreateFromPem selects the first certificate; Certbot fullchain.pem starts with the leaf.
			chain[0].Dispose();
			chain.RemoveAt(0);
			using (var validation = new X509Chain())
			{
				validation.ChainPolicy.ExtraStore.AddRange(chain);
				validation.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
				validation.ChainPolicy.DisableCertificateDownloads = true;
				// The server need not trust its own CA (e.g. a private deployment). Clients decide trust. Still
				// validate signatures, validity and usages, and ensure all supplied chain material belongs here.
				validation.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
				if (!validation.Build(leaf)) throw new InvalidDataException("Invalid certificate chain.");
				foreach (var intermediate in chain)
				{
					bool found = false;
					foreach (var element in validation.ChainElements)
						found |= element.Certificate.Thumbprint == intermediate.Thumbprint;
					if (!found) throw new InvalidDataException("fullchain.pem contains an unrelated certificate.");
				}
				if (validation.ChainElements.Count == 1 && leaf.Subject != leaf.Issuer)
					throw new InvalidDataException("fullchain.pem is missing the issuing certificate.");
			}
			var context = SslStreamCertificateContext.Create(leaf, chain, offline: true);
			if (pem != File.ReadAllText(CertificatePath) || key != File.ReadAllText(KeyPath))
				throw new IOException("Certificate files changed while being loaded; retrying on the next poll.");
			return new Generation { Leaf = leaf, Chain = chain, Context = context, Hash = hash };
		}
		catch
		{
			leaf?.Dispose();
			foreach (var certificate in chain) certificate.Dispose();
			throw;
		}
	}

	private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

	private void LogLoaded(Generation generation) => Logger.LogInformation(
		"Loaded HTTPS certificate {Fingerprint}, expires {Expiry:u}", generation.Leaf.Thumbprint, generation.Leaf.NotAfter.ToUniversalTime());

	/// <summary>Stops polling and releases certificates; call only after the web host has stopped.</summary>
	public async ValueTask DisposeAsync()
	{
		Stop.Cancel();
		if (PollTask != null) await PollTask;
		foreach (var generation in Generations) generation.Dispose();
		Stop.Dispose();
	}
}
