namespace SteamRoulette.Services;

public static class Logger
{
    public static event Action<string>? MessageLogged;

    public static void Log(string message)
    {
        Console.WriteLine(message);
        MessageLogged?.Invoke(message + Environment.NewLine);
    }
}
