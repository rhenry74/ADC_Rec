using Microsoft.UI.Xaml.Controls;
using ADC_Rec.Models.Filters;

namespace ADC_Rec.Controls.Filters
{
    public sealed partial class ReverbControl : UserControl
    {
        public ReverbFilter ViewModel { get; }

        public ReverbControl(ReverbFilter filter)
        {
            ViewModel = filter;
            this.InitializeComponent();
        }
    }
}
