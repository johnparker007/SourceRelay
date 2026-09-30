using System.Windows;
using Microsoft.Win32;
using SourceRelay.ViewModels;

namespace SourceRelay
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel = new();
        public MainWindow()
        {
            InitializeComponent();
            DataContext = _viewModel;
            Loaded += async (_, _) => await _viewModel.InitializeAsync();
            Closed += (_, _) => _viewModel.Dispose();
        }

        private async void ChooseFolder_Click(object sender, RoutedEventArgs e) { var dialog = new OpenFolderDialog { Title = "Choose the project root", Multiselect = false }; if (dialog.ShowDialog(this) == true) await _viewModel.ChooseProjectAsync(dialog.FolderName); }
        private async void Generate_Click(object sender, RoutedEventArgs e) => await _viewModel.GenerateAsync();
        private async void OpenReturned_Click(object sender, RoutedEventArgs e) { var dialog = new OpenFileDialog { Filter = "ZIP archives (*.zip)|*.zip" }; if (dialog.ShowDialog(this) == true) await _viewModel.LoadReturnedAsync(dialog.FileName); }
        private async void Apply_Click(object sender, RoutedEventArgs e) { if (_viewModel.Changes.Any(x => x.Apply && x.Change.Kind == Models.ChangeKind.LocalFileChanged) && MessageBox.Show(this, "One or more local files changed after export. Overwrite them?", "Confirm overwrite", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; await _viewModel.ApplyAsync(); }
        private async void Undo_Click(object sender, RoutedEventArgs e) { await _viewModel.UndoAsync(); if (_viewModel.Status.Contains("changed externally") && MessageBox.Show(this, _viewModel.Status + " Overwrite it?", "Confirm undo", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) await _viewModel.UndoAsync(true); }
        private async void Redo_Click(object sender, RoutedEventArgs e) { await _viewModel.RedoAsync(); if (_viewModel.Status.Contains("changed externally") && MessageBox.Show(this, _viewModel.Status + " Overwrite it?", "Confirm redo", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) await _viewModel.RedoAsync(true); }
        private void Dismiss_Click(object sender, RoutedEventArgs e) => _viewModel.Dismiss();
        private void OpenFolder_Click(object sender, RoutedEventArgs e) => _viewModel.OpenOutputFolder();
        private async void Window_Drop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length == 1) await _viewModel.LoadReturnedAsync(files[0]); }
        private void Window_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
        private void SelectionChanged(object sender, RoutedEventArgs e) => _viewModel.RefreshSelection();
    }
}
