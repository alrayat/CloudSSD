using CloudSSD.Core;
using CloudSSD.Games;

namespace CloudSSD.Tests;

public sealed class ImportDestinationTests
{
    private static MountDefinition Drive => new() { RemoteName = "terabox-chunked", RemotePath = "Library", DriveLetter = "T" };

    [Theory]
    [InlineData("T:\\SteamLibrary\\steamapps\\common\\Example", "SteamLibrary/steamapps/common/Example")]
    [InlineData("t:/SteamLibrary/steamapps/common/Example", "SteamLibrary/steamapps/common/Example")]
    [InlineData(" Games/Example ", "Games/Example")]
    [InlineData("/Games/Example/", "Games/Example")]
    public void Selected_drive_paths_map_inside_its_remote_root(string input, string folder)
    {
        Assert.Equal("terabox-chunked:Library/" + folder, GameImporter.Destination(Drive, input));
    }

    [Theory]
    [InlineData("G:/Games/Example")]
    [InlineData("T:Games/Example")]
    [InlineData("//server/share/Example")]
    [InlineData("Games/../Example")]
    [InlineData("other-remote:Example")]
    [InlineData("T:/")]
    public void Invalid_or_different_drive_destinations_are_rejected_before_upload(string input)
    {
        Assert.Throws<ArgumentException>(() => GameImporter.Destination(Drive, input));
    }
}
