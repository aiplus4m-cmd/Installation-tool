using System.Net;
using System.Windows;

namespace WinSetupHelper
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show("Đã xảy ra lỗi không mong muốn:\n\n" + args.Exception.Message,
                    "Windows Setup Helper", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };
            base.OnStartup(e);
        }
    }
}
