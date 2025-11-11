using MarketScanner.ViewModels;

namespace MarketScanner.Views;

public partial class AlgoRunnerPage : ContentPage
{
    public AlgoRunnerPage(AlgoRunnerViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}

