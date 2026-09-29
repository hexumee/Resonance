using Windows.UI.Xaml.Controls;

namespace App {
    public sealed partial class OfflineQueueDialog : ContentDialog {
        public OfflineQueueDialog() {
            InitializeComponent();
        }

        internal TextBlock Summary {
            get {
                return QueueSummary;
            }
        }

        internal ListView ItemsList {
            get {
                return QueueList;
            }
        }

        internal TextBlock Status {
            get {
                return QueueStatus;
            }
        }

        internal Button Send {
            get {
                return SendButton;
            }
        }

        internal Button Remove {
            get {
                return RemoveButton;
            }
        }
    }
}
