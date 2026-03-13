using Microsoft.UI.Xaml.Controls;
using ADC_Rec.Models.Filters;

namespace ADC_Rec.Controls.Filters
{
    public sealed partial class CompressorControl : UserControl
    {
        public CompressorFilter ViewModel { get; }

        public CompressorControl(CompressorFilter filter)
        {
            ViewModel = filter;
            this.InitializeComponent();
        }
    }
}
