using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using ADC_Rec.Models.Filters;

namespace ADC_Rec.Controls.Filters
{
    public sealed partial class FilterCardControl : UserControl
    {
        public event EventHandler<FilterCardControl> DeleteRequested;
        public IFilter Filter { get; }

        public FilterCardControl(IFilter filter)
        {
            this.InitializeComponent();
            Filter = filter;
            TitleBox.Text = filter.Name;
            TitleBox.TextChanged += (s, e) => { filter.Name = TitleBox.Text; };
            EnabledCheck.IsChecked = filter.IsEnabled;
            EnabledCheck.Checked += (s, e) => filter.IsEnabled = true;
            EnabledCheck.Unchecked += (s, e) => filter.IsEnabled = false;

            if (filter is PeakingEQFilter peakingFilter)
            {
                ParamPresenter.Content = new PeakingEQControl(peakingFilter);
            }
            
            // Build channel toggles
            foreach (ChannelBinding binding in Enum.GetValues(typeof(ChannelBinding)))
            {
                if (binding == ChannelBinding.None) continue;
                var tb = new ToggleButton 
                { 
                    Content = binding.ToString(), 
                    IsChecked = (filter.Channels & binding) != 0,
                    Margin = new Thickness(0, 0, 4, 0),
                    Padding = new Thickness(8, 2, 8, 2)
                };
                tb.Checked += (s, e) => { filter.Channels |= binding; };
                tb.Unchecked += (s, e) => { filter.Channels &= ~binding; };
                ChannelPanel.Children.Add(tb);
            }
        }

        private void CollapseToggle_Click(object sender, RoutedEventArgs e)
        {
            ContentPanel.Visibility = (CollapseToggle.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
            CollapseToggle.Content = (CollapseToggle.IsChecked == true) ? "▼" : "▶";
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            DeleteRequested?.Invoke(this, this);
        }
    }
}
