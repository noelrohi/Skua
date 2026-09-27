using Skua.Core.Models;

namespace Skua.Core.Utils;

public static class FlashTrustManager
{
    private static readonly string TrustFolderPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Macromedia", "Flash Player", "#Security", "FlashPlayerTrust");

    private static readonly string TrustFileName = "Skua.cfg";

    public static void EnsureTrustFile()
    {
        try
        {
            Directory.CreateDirectory(TrustFolderPath);

            string trustFilePath = Path.Combine(TrustFolderPath, TrustFileName);
            string appDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string skuaDataDirectory = ClientFileSources.SkuaDIR;

            string[] requiredPaths = { appDirectory, skuaDataDirectory };

            if (File.Exists(trustFilePath))
            {
                string[] existingPaths = File.ReadAllLines(trustFilePath);
                bool needsUpdate = false;

                foreach (string path in requiredPaths)
                {
                    bool found = false;
                    foreach (string existingPath in existingPaths)
                    {
                        if (existingPath.Trim().Equals(path, StringComparison.OrdinalIgnoreCase))
                        {
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                    {
                        needsUpdate = true;
                        break;
                    }
                }

                if (!needsUpdate)
                    return;
            }

            File.WriteAllLines(trustFilePath, requiredPaths);
        }
        catch
        {
            /* ignored */
        }
    }
}
