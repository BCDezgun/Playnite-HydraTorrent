using HydraTorrent.Models;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace HydraTorrent.Views
{
    /// <summary>
    /// Диалог выбора исполняемого файла для игры
    /// </summary>
    public partial class ExecutableSelectionWindow : UserControl
    {
        private readonly IPlayniteAPI _api;
        private readonly List<ExecutableCandidate> _candidates;
        private readonly string _gameName;
        private ExecutableCandidate _selectedCandidate;
        private Window _parentWindow;

        public ExecutableCandidate SelectedCandidate => _selectedCandidate;

        public ExecutableSelectionWindow(List<ExecutableCandidate> candidates, string gameName, IPlayniteAPI api)
        {
            InitializeComponent();

            _candidates = candidates ?? new List<ExecutableCandidate>();
            _gameName = gameName ?? "Unknown Game";
            _api = api;

            txtGameName.Text = _gameName;
            lstCandidates.ItemsSource = _candidates;

            // Выбираем первый кандидат по умолчанию
            Loaded += ExecutableSelectionWindow_Loaded;
        }

        private void ExecutableSelectionWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Получаем родительское окно
            _parentWindow = Window.GetWindow(this);

            // Фокус на первый элемент
            if (_candidates.Count > 0)
            {
                SelectFirstCandidate();
            }
        }

        private void SelectFirstCandidate()
        {
            var container = lstCandidates.ItemContainerGenerator.ContainerFromIndex(0);
            if (container != null)
            {
                var radio = FindVisualChild<RadioButton>(container);
                if (radio != null)
                {
                    radio.IsChecked = true;
                }
            }
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            // Находим выбранный RadioButton
            _selectedCandidate = GetSelectedCandidate();

            if (_selectedCandidate == null)
            {
                MessageBox.Show(
                    ResourceProvider.GetString("LOC_HydraTorrent_SelectExecutableHint"),
                    ResourceProvider.GetString("LOC_HydraTorrent_Attention"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (_parentWindow != null)
            {
                _parentWindow.DialogResult = true;
                _parentWindow.Close();
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_parentWindow != null)
            {
                _parentWindow.DialogResult = false;
                _parentWindow.Close();
            }
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            // Открыть диалог выбора файла
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
                Title = ResourceProvider.GetString("LOC_HydraTorrent_SelectExecutableFile")
            };

            if (_candidates.Count > 0 && !string.IsNullOrEmpty(_candidates[0].Directory))
            {
                dialog.InitialDirectory = _candidates[0].Directory;
            }

            if (dialog.ShowDialog() == true)
            {
                // Создаём нового кандидата из выбранного файла
                var fileInfo = new FileInfo(dialog.FileName);

                _selectedCandidate = new ExecutableCandidate
                {
                    FilePath = dialog.FileName,
                    FileName = fileInfo.Name,
                    FileSize = fileInfo.Length,
                    ConfidenceScore = 100, // Ручной выбор = 100%
                    ScoreReasons = new List<string>
                    {
                        ResourceProvider.GetString("LOC_HydraTorrent_ReasonManualSelection")
                    }
                };

                if (_parentWindow != null)
                {
                    _parentWindow.DialogResult = true;
                    _parentWindow.Close();
                }
            }
        }

        private ExecutableCandidate GetSelectedCandidate()
        {
            foreach (var item in lstCandidates.Items)
            {
                var container = lstCandidates.ItemContainerGenerator.ContainerFromItem(item);
                if (container != null)
                {
                    var radio = FindVisualChild<RadioButton>(container);
                    if (radio != null && radio.IsChecked == true)
                    {
                        return item as ExecutableCandidate;
                    }
                }
            }

            return null;
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;

            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);

                if (child is T typedChild)
                {
                    return typedChild;
                }

                var result = FindVisualChild<T>(child);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }
    }
}
