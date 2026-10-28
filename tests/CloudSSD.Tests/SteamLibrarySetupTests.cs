using CloudSSD.Games;

namespace CloudSSD.Tests;

public sealed class SteamLibrarySetupTests
{
    [Fact]
    public void Reads_escaped_Steam_library_paths_without_rewriting_the_client_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "cloudssd-cli-steam-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        const string text = "\"libraryfolders\" { \"1\" { \"path\" \"T:\\\\SteamLibrary\" } }";
        try
        {
            File.WriteAllText(vdf, text);
            Assert.True(SteamLibrarySetup.IsRegistered(root, @"t:\SteamLibrary\"));
            Assert.False(SteamLibrarySetup.IsRegistered(root, @"Y:\SteamLibrary"));
            Assert.Equal(text, File.ReadAllText(vdf));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Preparing_a_new_library_checks_writes_and_keeps_existing_game_data()
    {
        var root = Path.Combine(Path.GetTempPath(), "cloudssd-cli-steam-probe-" + Guid.NewGuid().ToString("N"));
        var game = Path.Combine(root, "SteamLibrary", "steamapps", "common", "existing.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(game)!);
        File.WriteAllBytes(game, [9, 8, 7]);
        try
        {
            var status = SteamLibrarySetup.Prepare(root, null);
            Assert.False(status.SteamInstalled);
            Assert.False(status.Registered);
            Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(game));
            Assert.Empty(Directory.GetFiles(status.Path, ".cloudssd-write-check-*"));
        }
        finally { Directory.Delete(root, true); }
    }
}
