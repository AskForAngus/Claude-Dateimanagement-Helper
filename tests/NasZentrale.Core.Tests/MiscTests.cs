using NasZentrale.Core.Paths;
using NasZentrale.Core.Protocol;
using NasZentrale.Core.TrueNas;
using Xunit;

namespace NasZentrale.Core.Tests;

public class MiscTests
{
    [Theory]
    [InlineData("nas.firma.local", "wss://nas.firma.local/api/current")]
    [InlineData("192.168.1.10", "wss://192.168.1.10/api/current")]
    [InlineData("https://nas:8443/", "wss://nas:8443/api/current")]
    [InlineData("http://nas", "ws://nas/api/current")]
    public void BuildUri_defaults_to_secure_websocket(string input, string expected)
    {
        Assert.Equal(expected, TrueNasClient.BuildUri(input).ToString());
    }

    [Fact]
    public void NasPath_helpers()
    {
        Assert.Equal("/mnt/tank", NasPath.Parent("/mnt/tank/daten/"));
        Assert.Equal("daten", NasPath.Name("/mnt/tank/daten"));
        Assert.True(NasPath.IsUnder("/mnt/tank/daten/x", "/mnt/tank/daten"));
        Assert.False(NasPath.IsUnder("/mnt/tank/datenarchiv", "/mnt/tank/daten"));
        Assert.Equal("x/y", NasPath.Relative("/mnt/tank/daten/x/y", "/mnt/tank/daten"));
        Assert.Equal(new[] { "/mnt/t", "/mnt/t/a", "/mnt/t/a/b" }, NasPath.Chain("/mnt/t", "/mnt/t/a/b"));
    }

    [Fact]
    public void ShareMapper_prefers_the_most_specific_share()
    {
        var mapper = new ShareMapper("nas", new[]
        {
            new SmbShareInfo(1, "Daten", "/mnt/tank/daten", true),
            new SmbShareInfo(2, "Buchhaltung", "/mnt/tank/daten/buchhaltung", true),
            new SmbShareInfo(3, "Alt", "/mnt/tank/alt", false),
        });

        Assert.Equal(@"\\nas\Daten\Projekte\2026", mapper.ToUnc("/mnt/tank/daten/Projekte/2026"));
        Assert.Equal(@"\\nas\Buchhaltung\Belege", mapper.ToUnc("/mnt/tank/daten/buchhaltung/Belege"));
        Assert.Equal(@"\\nas\Buchhaltung", mapper.ToUnc("/mnt/tank/daten/buchhaltung"));
        Assert.Null(mapper.ToUnc("/mnt/tank/alt/x"));
        Assert.Null(mapper.ToUnc("/mnt/tank/datenarchiv"));
    }

    [Fact]
    public void System_datasets_are_hidden()
    {
        Assert.True(TrueNasService.IsUserDataset(new DatasetInfo("tank/daten", "/mnt/tank/daten", "NFSV4", 0, 0)));
        Assert.False(TrueNasService.IsUserDataset(new DatasetInfo("tank/.system", "/mnt/tank/.system", "POSIX", 0, 0)));
        Assert.False(TrueNasService.IsUserDataset(new DatasetInfo("tank/ix-apps", "/mnt/.ix-apps", "POSIX", 0, 0)));
        Assert.False(TrueNasService.IsUserDataset(new DatasetInfo("boot-pool/ROOT", "/", "POSIX", 0, 0)));
    }

    [Fact]
    public async Task ProtocolStore_appends_and_reads_back()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nz-test-" + Guid.NewGuid());
        try
        {
            var store = new ProtocolStore(dir);
            var first = new ProtocolEntry { User = "FIRMA\\admin", Category = ProtocolCategory.Rights, Summary = "eins" };
            await store.AppendAsync(first);
            await store.AppendAsync(new ProtocolEntry { User = "FIRMA\\admin2", Summary = "zwei", UndoOf = first.Id, Time = first.Time.AddSeconds(1) });

            var all = await store.LoadAsync();
            Assert.Equal(2, all.Count);
            Assert.Equal("zwei", all[0].Summary);
            Assert.Equal(first.Id, all[0].UndoOf);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
