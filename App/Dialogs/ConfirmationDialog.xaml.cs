using Windows.UI.Xaml.Controls;

namespace App {
    public sealed partial class ConfirmationDialog : ContentDialog {
        public ConfirmationDialog() {
            InitializeComponent();
        }

        internal string Message {
            get {
                return DialogMessage.Text;
            }
            set {
                DialogMessage.Text = value;
            }
        }
    }
}
