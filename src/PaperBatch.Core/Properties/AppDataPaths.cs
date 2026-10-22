namespace PaperBatch.Core.Properties;

/// <summary>Resolves PaperBatch's per-user data root, with an explicit override for isolated sessions.</summary>
public static class AppDataPaths
{
    public const string LocalApplicationDataOverrideVariable = "PAPERBATCH_LOCAL_APP_DATA";

    public static string LocalApplicationData
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable(LocalApplicationDataOverrideVariable);
            return string.IsNullOrWhiteSpace(overridePath)
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : Path.GetFullPath(overridePath);
        }
    }
}
