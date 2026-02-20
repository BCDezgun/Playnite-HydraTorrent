using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace HydraTorrent
{
    /// <summary>
    /// Interaction logic for DownloadPathWindow.xaml
    /// </summary>
    public partial class DownloadPathWindow : UserControl
    {
        public string SelectedPath { get; private set; }
        private Window parentWindow;

        public DownloadPathWindow()
        {
            InitializeComponent();
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            // Открываем стандартный проводник Windows для выбора папки
            var dialog = new System.Windows.Forms.FolderBrowserDialog();
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                PathTextBox.Text = dialog.SelectedPath;
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            SelectedPath = PathTextBox.Text;
            parentWindow = Window.GetWindow(this);
            parentWindow.DialogResult = true;
            parentWindow.Close();
        }
    }
}
