namespace VAppCore.Tests;

public class ApiKeyHasherTests
{
    [Fact]
    public void GeneratePlaintext_StartsWithVkLivePrefix()
    {
        var key = ApiKeyHasher.GeneratePlaintext();
        Assert.StartsWith("vk_live_", key);
    }

    [Fact]
    public void GeneratePlaintext_ProducesDistinctValues()
    {
        var keys = Enumerable.Range(0, 100).Select(_ => ApiKeyHasher.GeneratePlaintext()).ToList();
        Assert.Equal(100, keys.Distinct().Count());
    }

    [Fact]
    public void GeneratePlaintext_HasFullLength_PrefixPlusFortyThreeChars()
    {
        var key = ApiKeyHasher.GeneratePlaintext();
        // "vk_live_" (8) + 43 base64-url chars = 51
        Assert.Equal(51, key.Length);
    }

    [Fact]
    public void Hash_IsDeterministic()
    {
        var hash1 = ApiKeyHasher.Hash("vk_live_abc");
        var hash2 = ApiKeyHasher.Hash("vk_live_abc");
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void Hash_IsLowerCaseHex_64Chars()
    {
        var hash = ApiKeyHasher.Hash("vk_live_abc");
        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void Hash_DifferentInputs_ProduceDifferentHashes()
    {
        var h1 = ApiKeyHasher.Hash("vk_live_a");
        var h2 = ApiKeyHasher.Hash("vk_live_b");
        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void ExtractPrefix_ReturnsFirstTwelveChars()
    {
        Assert.Equal("vk_live_a1b2", ApiKeyHasher.ExtractPrefix("vk_live_a1b2c3d4e5f6"));
    }

    [Fact]
    public void ExtractPrefix_HandlesShortKeys_ReturnsWholeString()
    {
        Assert.Equal("vk_live_", ApiKeyHasher.ExtractPrefix("vk_live_"));
    }
}
