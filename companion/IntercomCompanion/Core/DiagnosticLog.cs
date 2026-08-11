namespace IntercomCompanion.Core;

internal static class DiagnosticLog
{
    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(CompanionSettings.AppDataDirectory, "diagnostics.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(CompanionSettings.AppDataDirectory);
            lock (Gate)
                File.AppendAllText(FilePath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
    }
}
