using MarketScanner.ViewModels;

namespace MarketScanner.Views;

public partial class AlgoRunnerTile : ContentView
{
    public AlgoRunnerTile()
    {
        InitializeComponent();
    }

    public AlgoRunnerTile(AlgoRunnerViewModel viewModel) : this()
    {
        BindingContext = viewModel;
    }
}

