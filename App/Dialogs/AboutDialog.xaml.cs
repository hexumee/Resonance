using Windows.UI.Xaml.Controls;

namespace App {
    public sealed partial class AboutDialog : ContentDialog {
        public AboutDialog() {
            InitializeComponent();
        }

        internal string Body {
            get {
                return AboutText.Text;
            }
            set {
                AboutText.Text = value;
            }
        }
    }
}
