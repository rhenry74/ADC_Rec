using Microsoft.UI.Xaml.Controls;
using ADC_Rec.Models.Filters;

namespace ADC_Rec.Controls.Filters
{
    public sealed partial class DelayControl : UserControl
    {
        public DelayFilter ViewModel { get; }

        public DelayControl(DelayFilter filter)
        {
            ViewModel = filter;
            this.InitializeComponent();
            filter.PropertyChanged += (s, e) => { /* refresh logic if needed */ };
        }
    }
}
