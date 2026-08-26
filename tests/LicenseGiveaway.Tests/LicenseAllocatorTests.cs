public class LicenseAllocatorTests
{
    [Fact]
    public void AllocatesExactlyTheConfiguredNumber()
    {
        var allocator = new LicenseAllocator(1_000);

        for (var i = 0; i < 1_000; i++)
        {
            var result = allocator.Apply(
                $"user{i}@example.com",
                $"+358400000{i:D4}");

            Assert.Equal(ApplicationStatus.Accepted, result.Status);
        }

        var extra = allocator.Apply(
            "extra@example.com",
            "+358499999999");

        Assert.Equal(ApplicationStatus.SoldOut, extra.Status);
    }

    [Fact]
    public void SameEmailCannotReceiveTwoLicenses()
    {
        var allocator = new LicenseAllocator(1_000);

        var first = allocator.Apply("alice@example.com", "+358401111111");
        var second = allocator.Apply("ALICE@example.com", "+358402222222");

        Assert.Equal(ApplicationStatus.Accepted, first.Status);
        Assert.Equal(ApplicationStatus.Duplicate, second.Status);
    }

    [Fact]
    public void SamePhoneCannotReceiveTwoLicenses()
    {
        var allocator = new LicenseAllocator(1_000);

        var first = allocator.Apply("alice@example.com", "+358401111111");
        var second = allocator.Apply("bob@example.com", "+358 401 111 111");

        Assert.Equal(ApplicationStatus.Accepted, first.Status);
        Assert.Equal(ApplicationStatus.Duplicate, second.Status);
    }

    [Fact]
    public async Task ConcurrentApplicationsDoNotExceedLicenseLimit()
    {
        var allocator = new LicenseAllocator(1_000);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 10_000)
                .Select(i => Task.Run(() =>
                    allocator.Apply(
                        $"user{i}@example.com",
                        $"+3584{i:D9}"))));

        var accepted = results.Count(x => x.Status == ApplicationStatus.Accepted);

        Assert.Equal(1_000, accepted);
    }
}
