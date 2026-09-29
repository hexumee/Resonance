using Windows.ApplicationModel.Resources;
namespace App {
    internal static class AppText {
        internal static string Get(string key) {
            return ResourceLoader.GetForViewIndependentUse().GetString(key);
        }
    }
}
