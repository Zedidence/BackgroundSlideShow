using Windows.Storage;
using Windows.System.UserProfile;

namespace BackgroundSlideShow.Services;

public class LockScreenService
{
    /// <summary>
    /// Sets the Windows lock screen image to the specified file path.
    /// Returns true on success, false on failure (error is logged).
    /// </summary>
    public async Task<bool> SetLockScreenImageAsync(string imagePath)
    {
        try
        {
            AppLogger.Info($"SetLockScreenImageAsync → {imagePath}");
            var file = await StorageFile.GetFileFromPathAsync(imagePath);
            await LockScreen.SetImageFileAsync(file);
            AppLogger.Info("SetLockScreenImageAsync complete");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"SetLockScreenImageAsync failed for '{imagePath}'", ex);
            return false;
        }
    }
}
