using System.ComponentModel;
using System.Windows;
using DocDr.App.ViewModels;

namespace DocDr.App.Views;

public partial class OcrProgressWindow : Window
{
    private readonly OcrProgressViewModel _viewModel;
    private bool _finished;

    public OcrProgressWindow(OcrProgressViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        viewModel.Closed += () => { _finished = true; Close(); };
    }

    // The run keeps going after a cancel until the current page lands — don't let the
    // window close (Esc / X) until the host signals completion.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_finished)
        {
            e.Cancel = true;
            _viewModel.CancelCommand.Execute(null);
        }

        base.OnClosing(e);
    }
}
