using System.Windows;
using DocDr.App.ViewModels;

namespace DocDr.App.Views;

public partial class AnnotationEditorWindow : Window
{
    public AnnotationEditorWindow(AnnotationEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) =>
        {
            ContentsBox.Focus();
            ContentsBox.SelectAll();
        };
    }
}
