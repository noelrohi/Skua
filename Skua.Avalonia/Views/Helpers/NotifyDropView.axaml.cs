using Avalonia.Controls;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views.Helpers;

public partial class NotifyDropView : UserControl
{
    public NotifyDropView()
    {
        InitializeComponent();
        ListInput.RemoveOnDelete(DropList, () => Model?.RemoveDropsCommand, () => Model?.RemoveAllDropsCommand);
        ListInput.DigitsOnly(SoundCount);
        ListInput.DigitsOnly(SoundDelay);
    }

    private NotifyDropViewModel? Model => DataContext as NotifyDropViewModel;
}
