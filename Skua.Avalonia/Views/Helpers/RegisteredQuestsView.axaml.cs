using Avalonia.Controls;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views.Helpers;

public partial class RegisteredQuestsView : UserControl
{
    public RegisteredQuestsView()
    {
        InitializeComponent();
        ListInput.RemoveOnDelete(AutoQuestsList, () => Model?.RemoveQuestsCommand, () => Model?.RemoveAllQuestsCommand);
    }

    private RegisteredQuestsViewModel? Model => DataContext as RegisteredQuestsViewModel;
}
