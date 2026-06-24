using Microsoft.UI.Xaml.Controls;
using ADC_Rec.Models.Filters;

namespace ADC_Rec.Controls.Filters
{
    public sealed partial class NoiseSuppressionControl : UserControl
    {
        public NoiseSuppressionFilter ViewModel { get; }

        public NoiseSuppressionControl(NoiseSuppressionFilter filter)
        {
            ViewModel = filter;
            this.InitializeComponent();
        }
    }
}
