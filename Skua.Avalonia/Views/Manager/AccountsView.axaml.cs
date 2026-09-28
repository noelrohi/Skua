using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Avalonia.Manager;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.ViewModels;
using Skua.Core.ViewModels.Manager;

namespace Skua.Avalonia.Views.Manager;

/// <summary>Ported from <c>Skua.WPF/Views/AccountManager.xaml.cs</c>: the password box, tags and groups, which Core leaves to the view.</summary>
public partial class AccountsView : UserControl
{
    public AccountsView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            StrongReferenceMessenger.Default.Register<AccountsView, ClearPasswordBoxMessage>(this, (r, _) => r.Password.Text = "");
            WeakReferenceMessenger.Default.Register<AccountsView, AddAccountToGroupMessage>(this, (r, m) => r.AddToGroup([m.Account]));
            WeakReferenceMessenger.Default.Register<AccountsView, AddTagsMessage>(this, (r, m) => r.EditTags(m.Account));
        };
        DetachedFromVisualTree += (_, _) =>
        {
            StrongReferenceMessenger.Default.UnregisterAll(this);
            WeakReferenceMessenger.Default.UnregisterAll(this);
        };
    }

    private ManagerAccountsViewModel? Model => DataContext as ManagerAccountsViewModel;

    private IDialogService Dialogs => Model!.Dialogs;

    // Core's view model has a password setter only, so the box can't bind to it.
    private void OnPasswordChanged(object? sender, TextChangedEventArgs e)
    {
        if (Model is { } model)
            model.Accounts.PasswordInput = Password.Text ?? "";
    }

    private void OnNewGroup(object? sender, RoutedEventArgs e)
    {
        InputDialogViewModel input = new("Create Group", "Enter group name", "Group Name", numericInputOnly: false);
        if (Model is { } model && Dialogs.ShowDialog(input, "Create Group") == true && !string.IsNullOrWhiteSpace(input.DialogTextInput))
            model.Accounts.AddGroup(input.DialogTextInput.Trim());
    }

    private void OnTagSelected(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
            return;
        List<AccountItemViewModel> selected = [.. model.Accounts.Accounts.Where(a => a.UseCheck)];
        if (selected.Count == 0)
        {
            Dialogs.ShowMessageBox("No accounts selected", "Add Tags");
            return;
        }
        InputDialogViewModel input = new("Add Tags to Selected", "Enter tags (comma-separated)", "Tags", numericInputOnly: false);
        if (Dialogs.ShowDialog(input, "Add Tags") != true)
            return;
        foreach (AccountItemViewModel account in selected)
        {
            foreach (string tag in Tags(input.DialogTextInput).Where(t => !account.Tags.Contains(t)))
                account.Tags.Add(tag);
        }
        SaveTags(model);
    }

    private void OnGroupSelected(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
            return;
        List<AccountItemViewModel> selected = [.. model.Accounts.Accounts.Where(a => a.UseCheck)];
        if (selected.Count == 0)
            Dialogs.ShowMessageBox("No accounts selected", "Add to Group");
        else
            AddToGroup(selected);
    }

    private void OnRemoveFromGroup(object? sender, RoutedEventArgs e)
    {
        if (sender is global::Avalonia.Controls.Control { DataContext: AccountItemViewModel account } control
            && control.FindLogicalAncestorOfType<Expander>()?.DataContext is GroupItemViewModel group)
            WeakReferenceMessenger.Default.Send(new RemoveAccountFromGroupMessage(group, account));
    }

    private void EditTags(AccountItemViewModel account)
    {
        if (Model is not { } model)
            return;
        InputDialogViewModel input = new("Edit Tags", "Enter tags (comma-separated)", "Tags", numericInputOnly: false)
        {
            DialogTextInput = string.Join(", ", account.Tags),
        };
        if (Dialogs.ShowDialog(input, "Edit Tags") != true)
            return;
        account.Tags.Clear();
        foreach (string tag in Tags(input.DialogTextInput))
            account.Tags.Add(tag);
        SaveTags(model);
    }

    private void AddToGroup(IReadOnlyList<AccountItemViewModel> accounts)
    {
        if (Model is not { } model)
            return;
        SelectGroupDialogViewModel dialog = new(model.Accounts.Groups);
        if (Dialogs.ShowDialog(dialog, "Add to Group") != true || dialog.SelectedGroup is not { } group)
            return;
        foreach (AccountItemViewModel account in accounts)
            model.Accounts.AddAccountToGroup(account, group);
    }

    private static void SaveTags(ManagerAccountsViewModel model)
    {
        model.Accounts.SaveAccounts();
        model.Accounts.RefreshTagFilters();
        model.Accounts.ApplyTagFilter();
    }

    private static List<string> Tags(string text) =>
        [.. text.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).Distinct()];
}
