using Microsoft.UI.Xaml.Controls;
using ADC_Rec.Models.Filters;

namespace ADC_Rec.Controls.Filters
{
    public sealed partial class ShelfControl : UserControl
    {
        public ShelfFilter ViewModel { get; }

        public ShelfControl(ShelfFilter filter)
        {
            this.InitializeComponent();
            ViewModel = filter;
        }
    }
}
