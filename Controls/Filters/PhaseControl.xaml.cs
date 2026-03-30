using Microsoft.UI.Xaml.Controls;
using ADC_Rec.Models.Filters;

namespace ADC_Rec.Controls.Filters
{
    public sealed partial class PhaseControl : UserControl
    {
        public PhaseFilter ViewModel { get; }
        public PhaseControl(PhaseFilter filter)
        {
            ViewModel = filter;
            this.InitializeComponent();
        }
    }
}
