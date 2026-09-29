using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;

namespace App {
    internal static class AppShell {
        internal static void Attach(Frame frame) {
            frame.Navigated += (sender, args) => {
                SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility = frame.CanGoBack ? AppViewBackButtonVisibility.Visible : AppViewBackButtonVisibility.Collapsed;
            };

            SystemNavigationManager.GetForCurrentView().BackRequested += (sender, args) => {
                if (args.Handled) {
                    return;
                }

                bool handled = (frame.Content as MainPage)?.HandleBack() == true || 
                               (frame.Content as LibraryPage)?.HandleBack() == true || 
                               (frame.Content as MicrophoneSettingsPage)?.HandleBack() == true || 
                               (frame.Content as DetailsPage)?.HandleBack() == true || 
                               (frame.Content as AppSettingsPage)?.HandleBack() == true;
                if (handled) {
                    args.Handled = true;
                } else if (frame.CanGoBack) {
                    args.Handled = true;
                    frame.GoBack();
                }
            };
        }

        internal static async Task StopAsync(Frame frame) {
            if (frame?.Content is MainPage) {
                await ((MainPage)frame.Content).StopAsync();
            } else if (frame?.Content is MicrophoneSettingsPage) {
                await ((MicrophoneSettingsPage)frame.Content).StopAsync();
            } else if (frame?.Content is LibraryPage) {
                await ((LibraryPage)frame.Content).StopAsync();
            } else if (frame?.Content is DetailsPage) {
                await ((DetailsPage)frame.Content).StopAsync();
            } else if (frame?.Content is AppSettingsPage) {
                await ((AppSettingsPage)frame.Content).StopAsync();
            }

            try {
                await LibraryTools.PendingSave;
            } catch {

            }
        }

        internal static void Resume(Frame frame) {
            (frame?.Content as DetailsPage)?.Resume();
        }
    }
}
