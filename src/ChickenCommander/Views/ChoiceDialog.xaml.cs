using System.Windows;
using System.Windows.Controls;

namespace ChickenCommander.Views;

/// <summary>A message with any number of buttons; returns the index of the clicked button (or the cancel index).</summary>
public partial class ChoiceDialog : Window
{
    public ChoiceDialog(string title, string message, string details, IReadOnlyList<string> buttons, int defaultIndex, int cancelIndex)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        DetailsText.Text = details;
        DetailsText.Visibility = string.IsNullOrEmpty(details) ? Visibility.Collapsed : Visibility.Visible;
        Choice = cancelIndex;

        for (var i = 0; i < buttons.Count; i++)
        {
            var index = i;
            var button = new Button
            {
                Content = buttons[i],
                MinWidth = 90,
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(6, 4, 0, 0),
                IsDefault = i == defaultIndex,
                IsCancel = i == cancelIndex,
            };
            button.Click += (_, _) =>
            {
                Choice = index;
                DialogResult = true;
            };
            Buttons.Children.Add(button);
            if (i == defaultIndex) Loaded += (_, _) => button.Focus();
        }
    }

    public int Choice { get; private set; }
}
