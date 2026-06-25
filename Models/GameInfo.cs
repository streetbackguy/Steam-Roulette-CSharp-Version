namespace SteamRoulette.Models;

public class GameInfo
{
    public string AppId { get; set; } = "";
    public string Name { get; set; } = "";
    public string LibraryPath { get; set; } = "";
    public string SteamPath { get; set; } = "";   // root Steam install dir, for local asset lookup
    public string ImgIconUrl { get; set; } = "";
    public bool IsInstalled { get; set; } = true;
}
