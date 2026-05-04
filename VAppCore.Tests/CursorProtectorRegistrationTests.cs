using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VAppCore.Tests.Infrastructure;

namespace VAppCore.Tests;

public class CursorProtectorRegistrationTests
{
    [Fact]
    public void NoKey_NoCustomProtector_RegistersNoOp()
    {
        var services = new ServiceCollection();
        services.AddDbContext<VanillaDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddVAppCore<VanillaDbContext, Guid, Guid>();

        var sp = services.BuildServiceProvider();
        var protector = sp.GetRequiredService<ICursorProtector>();
        Assert.IsType<NoOpCursorProtector>(protector);
    }

    [Fact]
    public void KeyConfigured_RegistersAesGcm()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var keyBase64 = Convert.ToBase64String(key);

        var services = new ServiceCollection();
        services.AddDbContext<VanillaDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddVAppCore<VanillaDbContext, Guid, Guid>(o => o.CursorEncryptionKeys = [keyBase64]);

        var sp = services.BuildServiceProvider();
        var protector = sp.GetRequiredService<ICursorProtector>();
        Assert.IsType<AesGcmCursorProtector>(protector);
    }

    [Fact]
    public void EmptyKeysList_RegistersNoOp()
    {
        var services = new ServiceCollection();
        services.AddDbContext<VanillaDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddVAppCore<VanillaDbContext, Guid, Guid>(o => o.CursorEncryptionKeys = []);

        var sp = services.BuildServiceProvider();
        var protector = sp.GetRequiredService<ICursorProtector>();
        Assert.IsType<NoOpCursorProtector>(protector);
    }

    [Fact]
    public void MultipleKeys_RegistersAesGcmAcceptingRotation()
    {
        var b1 = new byte[32]; Random.Shared.NextBytes(b1);
        var b2 = new byte[32]; Random.Shared.NextBytes(b2);
        var k1 = Convert.ToBase64String(b1);
        var k2 = Convert.ToBase64String(b2);

        var services = new ServiceCollection();
        services.AddDbContext<VanillaDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddVAppCore<VanillaDbContext, Guid, Guid>(o => o.CursorEncryptionKeys = [k1, k2]);

        var sp = services.BuildServiceProvider();
        var protector = sp.GetRequiredService<ICursorProtector>();
        Assert.IsType<AesGcmCursorProtector>(protector);
    }

    [Fact]
    public void CustomProtector_DI_WinsOverDefault()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICursorProtector, FakeProtector>();
        services.AddDbContext<VanillaDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddVAppCore<VanillaDbContext, Guid, Guid>();

        var sp = services.BuildServiceProvider();
        var protector = sp.GetRequiredService<ICursorProtector>();
        Assert.IsType<FakeProtector>(protector);
    }

    [Fact]
    public void CursorCodec_AlwaysRegistered()
    {
        var services = new ServiceCollection();
        services.AddDbContext<VanillaDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddVAppCore<VanillaDbContext, Guid, Guid>();

        var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<CursorCodec>());
    }

    private class FakeProtector : ICursorProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] ciphertext) => ciphertext;
    }
}
