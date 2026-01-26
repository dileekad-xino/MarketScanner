using MarketScanner.ViewModels;

namespace MarketScanner.Views;

public partial class DailyPlWindowPage : ContentPage
{
    public DailyPlWindowPage()
    {
        InitializeComponent();
    }

    public DailyPlWindowPage(DailyPlViewModel viewModel) : this()
    {
        BindingContext = viewModel;
    }
}
