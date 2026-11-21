using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.Domain.Tenants;

namespace EnterpriseCommerce.TenantMerchant.UnitTests.Domain;

public sealed class ValueObjectTests
{
    [Theory]
    [InlineData("acme")]
    [InlineData("a1b")]
    [InlineData("acme-store-2")]
    [InlineData("123")]
    public void TenantSlug_AcceptsLowercaseHandles(string value)
    {
        Assert.True(TenantSlug.TryCreate(value, out var slug));
        Assert.Equal(value, slug.Value);
    }

    [Fact]
    public void TenantSlug_AcceptsTheMaximumLength()
    {
        Assert.True(TenantSlug.TryCreate(new string('a', TenantSlug.MaxLength), out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("Acme")]
    [InlineData(" acme")]
    [InlineData("acme ")]
    [InlineData("-acme")]
    [InlineData("acme-")]
    [InlineData("ac--me")]
    [InlineData("ac_me")]
    [InlineData("acmé")]
    [InlineData("ac.me")]
    public void TenantSlug_RejectsEverythingElseWithoutNormalizing(string? value)
    {
        Assert.False(TenantSlug.TryCreate(value, out _));
    }

    [Fact]
    public void TenantSlug_RejectsValuesOverTheMaximumLength()
    {
        Assert.False(TenantSlug.TryCreate(new string('a', TenantSlug.MaxLength + 1), out _));
    }

    [Fact]
    public void MerchantDisplayName_IsTrimmed()
    {
        Assert.True(MerchantDisplayName.TryCreate("  Acme Store  ", out var name));
        Assert.Equal("Acme Store", name.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Acme\nStore")]
    [InlineData("Acme\u0000")]
    public void MerchantDisplayName_RejectsEmptyAndControlCharacters(string? value)
    {
        Assert.False(MerchantDisplayName.TryCreate(value, out _));
    }

    [Fact]
    public void MerchantDisplayName_RejectsValuesOverTheMaximumLength()
    {
        Assert.True(MerchantDisplayName.TryCreate(new string('x', MerchantDisplayName.MaxLength), out _));
        Assert.False(MerchantDisplayName.TryCreate(new string('x', MerchantDisplayName.MaxLength + 1), out _));
    }

    [Theory]
    [InlineData("0f8fad5b-d9cb-469f-a165-70867728950e")]
    [InlineData("opaque|subject")]
    public void SubjectId_AcceptsOpaqueIdentifiers(string value)
    {
        Assert.True(SubjectId.TryCreate(value, out var subject));
        Assert.Equal(value, subject.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("with space")]
    [InlineData("tab\t")]
    public void SubjectId_RejectsEmptyAndWhitespace(string? value)
    {
        Assert.False(SubjectId.TryCreate(value, out _));
    }

    [Fact]
    public void SubjectId_RejectsValuesOverTheMaximumLength()
    {
        Assert.False(SubjectId.TryCreate(new string('s', SubjectId.MaxLength + 1), out _));
    }

    [Theory]
    [InlineData("admin:42")]
    [InlineData("A.b_c-1")]
    public void ActorReference_AcceptsTheIdentityRule(string value)
    {
        Assert.True(ActorReference.TryCreate(value, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("admin 42")]
    [InlineData("admin@example.com")]
    public void ActorReference_RejectsOtherCharacters(string? value)
    {
        Assert.False(ActorReference.TryCreate(value, out _));
    }
}
