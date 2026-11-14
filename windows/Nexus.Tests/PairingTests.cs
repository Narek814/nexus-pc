using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Nexus.Core.Pairing;
using Nexus.Core.Security;

namespace Nexus.Tests;

public class PairingTests
{
    private const string Fingerprint = "AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static byte[] NewKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return key.ExportSubjectPublicKeyInfo();
    }

    [Fact]
    public async Task Correct_code_and_approval_pairs_the_device_with_default_permissions()
    {
        var store = new InMemoryDeviceStore();
        var pairing = new PairingManager(store, TimeProvider.System);
        var ticket = pairing.Start();
        var key = NewKey();

        var submit = pairing.SubmitAsync(ticket.Code, "iPhone", key, Fingerprint, CancellationToken.None);
        var pending = await pairing.WaitForRequestAsync(ticket.SessionId, CancellationToken.None).WaitAsync(Wait);

        Assert.Equal("iPhone", pending!.DeviceName);
        Assert.Equal(DeviceCrypto.ShortAuthenticationString(key, Fingerprint), pending.VerificationCode);
        Assert.True(pairing.Decide(ticket.SessionId, approve: true));

        var result = await submit.WaitAsync(Wait);
        Assert.Equal(PairingStatus.Approved, result.Status);
        var device = Assert.Single(store.All);
        Assert.Equal(result.Device, device);
        Assert.Equal(Permissions.DefaultAfterPairing, device.Permissions);
    }

    [Fact]
    public async Task Rejection_stores_nothing_and_ends_the_session()
    {
        var store = new InMemoryDeviceStore();
        var pairing = new PairingManager(store, TimeProvider.System);
        var ticket = pairing.Start();

        var submit = pairing.SubmitAsync(ticket.Code, "iPhone", NewKey(), Fingerprint, CancellationToken.None);
        await pairing.WaitForRequestAsync(ticket.SessionId, CancellationToken.None).WaitAsync(Wait);
        Assert.True(pairing.Decide(ticket.SessionId, approve: false));

        Assert.Equal(PairingStatus.Rejected, (await submit.WaitAsync(Wait)).Status);
        Assert.Empty(store.All);
        Assert.Equal(PairingStatus.NoActiveSession, (await pairing.SubmitAsync(ticket.Code, "x", NewKey(), Fingerprint, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Five_wrong_codes_close_the_session()
    {
        var pairing = new PairingManager(new InMemoryDeviceStore(), TimeProvider.System);
        var ticket = pairing.Start();
        var wrong = ticket.Code == "00000000" ? "11111111" : "00000000";

        for (var i = 0; i < PairingManager.MaxCodeAttempts; i++)
        {
            Assert.Equal(PairingStatus.InvalidCode, (await pairing.SubmitAsync(wrong, "x", NewKey(), Fingerprint, CancellationToken.None)).Status);
        }
        // Even the right code no longer works: brute force cannot continue.
        Assert.Equal(PairingStatus.NoActiveSession, (await pairing.SubmitAsync(ticket.Code, "x", NewKey(), Fingerprint, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Second_device_cannot_submit_while_one_is_waiting_for_approval()
    {
        var pairing = new PairingManager(new InMemoryDeviceStore(), TimeProvider.System);
        var ticket = pairing.Start();
        var first = pairing.SubmitAsync(ticket.Code, "first", NewKey(), Fingerprint, CancellationToken.None);

        Assert.Equal(PairingStatus.Busy, (await pairing.SubmitAsync(ticket.Code, "second", NewKey(), Fingerprint, CancellationToken.None)).Status);

        pairing.Cancel(ticket.SessionId);
        Assert.Equal(PairingStatus.Expired, (await first.WaitAsync(Wait)).Status);
    }

    [Fact]
    public async Task Session_expires_on_its_own()
    {
        var pairing = new PairingManager(new InMemoryDeviceStore(), TimeProvider.System);
        var ticket = pairing.Start(TimeSpan.FromMilliseconds(200));

        Assert.Null(await pairing.WaitForRequestAsync(ticket.SessionId, CancellationToken.None).WaitAsync(Wait));
        Assert.Equal(PairingStatus.NoActiveSession, (await pairing.SubmitAsync(ticket.Code, "x", NewKey(), Fingerprint, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Pending_request_expires_if_nobody_decides()
    {
        var pairing = new PairingManager(new InMemoryDeviceStore(), TimeProvider.System);
        var ticket = pairing.Start(TimeSpan.FromMilliseconds(300));

        var result = await pairing.SubmitAsync(ticket.Code, "x", NewKey(), Fingerprint, CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(PairingStatus.Expired, result.Status);
        Assert.False(pairing.Decide(ticket.SessionId, approve: true));
    }

    [Fact]
    public async Task Phone_disconnecting_while_waiting_cancels_the_request()
    {
        var store = new InMemoryDeviceStore();
        var pairing = new PairingManager(store, TimeProvider.System);
        var ticket = pairing.Start();
        using var phone = new CancellationTokenSource();

        var submit = pairing.SubmitAsync(ticket.Code, "x", NewKey(), Fingerprint, phone.Token);
        await pairing.WaitForRequestAsync(ticket.SessionId, CancellationToken.None).WaitAsync(Wait);
        await phone.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit);
        Assert.False(pairing.Decide(ticket.SessionId, approve: true));
        Assert.Empty(store.All);
    }

    [Fact]
    public async Task Starting_a_new_session_invalidates_the_old_code()
    {
        var pairing = new PairingManager(new InMemoryDeviceStore(), TimeProvider.System);
        var old = pairing.Start();
        var current = pairing.Start();

        Assert.NotEqual(old.SessionId, current.SessionId);
        if (old.Code != current.Code)
        {
            Assert.Equal(PairingStatus.InvalidCode, (await pairing.SubmitAsync(old.Code, "x", NewKey(), Fingerprint, CancellationToken.None)).Status);
        }
        Assert.Null(await pairing.WaitForRequestAsync(old.SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task Device_limit_is_reported()
    {
        var pairing = new PairingManager(new InMemoryDeviceStore(limit: 0), TimeProvider.System);
        var ticket = pairing.Start();
        var submit = pairing.SubmitAsync(ticket.Code, "x", NewKey(), Fingerprint, CancellationToken.None);
        await pairing.WaitForRequestAsync(ticket.SessionId, CancellationToken.None).WaitAsync(Wait);
        pairing.Decide(ticket.SessionId, approve: true);
        Assert.Equal(PairingStatus.DeviceLimitReached, (await submit.WaitAsync(Wait)).Status);
    }
}

public class DeviceCryptoTests
{
    [Fact]
    public void Signature_over_transcript_verifies_and_is_bound_to_every_field()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        var pc = Guid.NewGuid();
        var device = Guid.NewGuid();
        var nonce = RandomNumberGenerator.GetBytes(DeviceCrypto.NonceBytes);
        const string fp = "AA00AA00AA00AA00AA00AA00AA00AA00AA00AA00AA00AA00AA00AA00AA00AA00";

        var signature = key.SignData(DeviceCrypto.AuthTranscript(pc, device, nonce, fp), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        Assert.True(DeviceCrypto.VerifySignature(spki, DeviceCrypto.AuthTranscript(pc, device, nonce, fp), signature));
        Assert.False(DeviceCrypto.VerifySignature(spki, DeviceCrypto.AuthTranscript(Guid.NewGuid(), device, nonce, fp), signature));
        Assert.False(DeviceCrypto.VerifySignature(spki, DeviceCrypto.AuthTranscript(pc, Guid.NewGuid(), nonce, fp), signature));
        Assert.False(DeviceCrypto.VerifySignature(spki, DeviceCrypto.AuthTranscript(pc, device, RandomNumberGenerator.GetBytes(32), fp), signature));
        Assert.False(DeviceCrypto.VerifySignature(spki, DeviceCrypto.AuthTranscript(pc, device, nonce, fp.Replace('A', 'B')), signature));
    }

    [Fact]
    public void Signature_from_another_key_fails()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var data = "x"u8.ToArray();
        var signature = other.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.False(DeviceCrypto.VerifySignature(key.ExportSubjectPublicKeyInfo(), data, signature));
    }

    [Fact]
    public void Only_p256_keys_are_accepted()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var rsa = RSA.Create(2048);
        Assert.False(DeviceCrypto.IsValidP256PublicKey(p384.ExportSubjectPublicKeyInfo()));
        Assert.False(DeviceCrypto.IsValidP256PublicKey(rsa.ExportSubjectPublicKeyInfo()));
        Assert.False(DeviceCrypto.IsValidP256PublicKey([1, 2, 3]));

        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = p256.ExportSubjectPublicKeyInfo();
        Assert.True(DeviceCrypto.IsValidP256PublicKey(spki));
        Assert.False(DeviceCrypto.IsValidP256PublicKey([.. spki, 0])); // trailing bytes
    }

    [Fact]
    public void Verification_code_is_six_digits_and_depends_on_key_and_certificate()
    {
        using var a = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var b = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string fp1 = "1111111111111111111111111111111111111111111111111111111111111111";
        const string fp2 = "2222222222222222222222222222222222222222222222222222222222222222";

        var sas = DeviceCrypto.ShortAuthenticationString(a.ExportSubjectPublicKeyInfo(), fp1);
        Assert.Matches("^[0-9]{6}$", sas);
        Assert.Equal(sas, DeviceCrypto.ShortAuthenticationString(a.ExportSubjectPublicKeyInfo(), fp1.ToLowerInvariant()));
        // A collision for one changed input has probability 1e-6; requiring both to collide makes a false failure ~1e-12.
        Assert.False(sas == DeviceCrypto.ShortAuthenticationString(b.ExportSubjectPublicKeyInfo(), fp1)
            && sas == DeviceCrypto.ShortAuthenticationString(a.ExportSubjectPublicKeyInfo(), fp2));
    }

    [Fact]
    public void Certificate_fingerprint_is_sha256_hex()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var cert = new CertificateRequest("CN=test", key, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(cert.RawData)), DeviceCrypto.CertificateFingerprint(cert));
    }
}

public class PairingLinkTests
{
    private static PairingLink Sample() => new(
        Guid.Parse("7d1c3b0a-5f7e-4c1e-9a52-2f0d9c4e8b11"), "Игровой ПК & co", ["192.168.1.20", "100.101.102.103"], 47800,
        "AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12", "04817263");

    [Fact]
    public void Round_trips()
    {
        var link = Sample();
        Assert.True(PairingLink.TryParse(link.ToString(), out var parsed));
        Assert.Equal(link.PcId, parsed.PcId);
        Assert.Equal(link.PcName, parsed.PcName);
        Assert.Equal(link.Hosts, parsed.Hosts);
        Assert.Equal((link.Port, link.CertificateFingerprint, link.Code), (parsed.Port, parsed.CertificateFingerprint, parsed.Code));
    }

    [Theory]
    [InlineData("https://evil.example/pair?v=1")]
    [InlineData("nexuspc://pair?v=2&pc=7d1c3b0a-5f7e-4c1e-9a52-2f0d9c4e8b11&name=x&h=127.0.0.1&p=1&fp=AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12&c=04817263")]
    [InlineData("nexuspc://pair?v=1&pc=7d1c3b0a-5f7e-4c1e-9a52-2f0d9c4e8b11&name=x&h=evil.example&p=1&fp=AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12&c=04817263")]
    [InlineData("nexuspc://pair?v=1&pc=7d1c3b0a-5f7e-4c1e-9a52-2f0d9c4e8b11&name=x&h=127.0.0.1&p=70000&fp=AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12&c=04817263")]
    [InlineData("nexuspc://pair?v=1&pc=7d1c3b0a-5f7e-4c1e-9a52-2f0d9c4e8b11&name=x&h=127.0.0.1&p=1&fp=short&c=04817263")]
    [InlineData("nexuspc://pair?v=1&pc=7d1c3b0a-5f7e-4c1e-9a52-2f0d9c4e8b11&name=x&h=127.0.0.1&p=1&fp=AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12&c=123")]
    [InlineData("nexuspc://pair?v=1&v=1&pc=7d1c3b0a-5f7e-4c1e-9a52-2f0d9c4e8b11&name=x&h=127.0.0.1&p=1&fp=AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12&c=04817263")]
    public void Rejects_invalid_links(string text)
    {
        Assert.False(PairingLink.TryParse(text, out _));
    }
}
