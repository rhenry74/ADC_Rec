using Microsoft.UI.Xaml.Controls;
using ADC_Rec.Models.Filters;

namespace ADC_Rec.Controls.Filters
{
    public sealed partial class PeakingEQControl : UserControl
    {
        public PeakingEQFilter ViewModel { get; }

        public PeakingEQControl(PeakingEQFilter filter)
        {
            ViewModel = filter;
            this.InitializeComponent();
            filter.PropertyChanged += (s, e) => { /* refresh logic if needed */ };
        }
    }
}
