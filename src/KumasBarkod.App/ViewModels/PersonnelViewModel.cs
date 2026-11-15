using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KumasBarkod.Core.ServerApi;
using KumasBarkod.Core.Sync;
using Wpf.Ui.Controls;

namespace KumasBarkod.App.ViewModels;

public sealed record UserRowView(long UserId, string Name, string Role, string RoleText, string Badge, bool Active, string StatusText);

/// <summary>
/// Personel (yönetim bilgisayarı): kart kodu + PIN ile giriş yapan depo personeli ve yetkililer.
/// Hiç personel yokken ilk yetkili girişsiz oluşturulur; sonrasında yalnızca yetkili yönetir.
/// Personel silinmez, pasif yapılır (geçmiş kayıtlarda adı kalır). PIN düz saklanmaz.
/// </summary>
public partial class PersonnelViewModel : ObservableObject
{
    private readonly DepotApiClient _api;

    public PersonnelViewModel(DepotApiClient api)
    {
        _api = api;
        _api.SessionChanged += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(async () => await RefreshStateAsync());
    }

    public ObservableCollection<UserRowView> Users { get; } = [];

    [ObservableProperty] public partial bool IsConfigured { get; set; } = true;
    [ObservableProperty] public partial bool BootstrapNeeded { get; set; }
    [ObservableProperty] public partial bool NeedsLogin { get; set; }
    [ObservableProperty] public partial bool IsManaging { get; set; }
    [ObservableProperty] public partial string UserText { get; set; } = "";

    // Giriş
    [ObservableProperty] public partial string LoginBadge { get; set; } = "";
    [ObservableProperty] public partial string LoginPin { get; set; } = "";

    // Yeni personel (ilk yetkili de bu formla)
    [ObservableProperty] public partial string NewName { get; set; } = "";
    [ObservableProperty] public partial string NewBadge { get; set; } = "";
    [ObservableProperty] public partial string NewPin { get; set; } = "";
    [ObservableProperty] public partial bool NewIsSupervisor { get; set; }

    // Seçili personel
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetPinCommand), nameof(ToggleActiveCommand), nameof(ToggleRoleCommand))]
    public partial UserRowView? SelectedUser { get; set; }
    [ObservableProperty] public partial string ResetPinText { get; set; } = "";

    [ObservableProperty] public partial bool MessageIsOpen { get; set; }
    [ObservableProperty] public partial string MessageTitle { get; set; } = "";
    [ObservableProperty] public partial string MessageText { get; set; } = "";
    [ObservableProperty] public partial InfoBarSeverity MessageSeverity { get; set; }

    public async Task ActivateAsync() => await RefreshStateAsync();

    private async Task RefreshStateAsync()
    {
        IsConfigured = _api.IsConfigured;
        if (!IsConfigured) { NeedsLogin = IsManaging = BootstrapNeeded = false; return; }
        try
        {
            BootstrapNeeded = (await _api.BootstrapNeededAsync()).Needed;
        }
        catch (SyncException ex)
        {
            Show(InfoBarSeverity.Warning, "Depo sunucusuna ulaşılamıyor", ex.Message);
            return;
        }
        var user = _api.CurrentUser;
        UserText = user is null ? "" : $"{user.Name} · {UserRoles.Display(user.Role)}";
        IsManaging = !BootstrapNeeded && user?.Role == UserRoles.Supervisor;
        NeedsLogin = !BootstrapNeeded && !IsManaging;
        if (BootstrapNeeded) NewIsSupervisor = true;
        if (IsManaging) await LoadUsersAsync();
        else Users.Clear();
    }

    private async Task LoadUsersAsync()
    {
        var selected = SelectedUser?.UserId;
        Users.Clear();
        foreach (var u in await _api.GetUsersAsync())
            Users.Add(new UserRowView(u.UserId, u.Name, u.Role, UserRoles.Display(u.Role), u.BadgeCode, u.Active, u.Active ? "Etkin" : "Pasif"));
        SelectedUser = Users.FirstOrDefault(u => u.UserId == selected);
    }

    [RelayCommand]
    private Task LoginAsync() => Run(async () =>
    {
        var user = await _api.LoginAsync(LoginBadge, LoginPin);
        LoginBadge = LoginPin = "";
        return user.Role == UserRoles.Supervisor
            ? ("Giriş yapıldı", user.Name)
            : ("Yetkili değil", $"{user.Name} depo personeli; personel yönetimi için yetkili girişi gerekir.");
    }, warnOnly: true);

    [RelayCommand]
    private Task LogoutAsync() => Run(async () =>
    {
        await _api.LogoutAsync();
        return ("Çıkış yapıldı", "");
    });

    [RelayCommand]
    private Task CreateAsync() => Run(async () =>
    {
        var first = BootstrapNeeded;
        var role = first || NewIsSupervisor ? UserRoles.Supervisor : UserRoles.Warehouse;
        var u = await _api.CreateUserAsync(new UserCreateRequest(NewName, role, NewBadge, NewPin));
        NewName = NewBadge = NewPin = "";
        NewIsSupervisor = false;
        await RefreshStateAsync();
        return (first ? "İlk yetkili oluşturuldu" : "Personel eklendi",
            first ? $"{u.Name} — şimdi kart kodu ({u.BadgeCode}) ve PIN ile giriş yapın."
                  : $"{u.Name} · {UserRoles.Display(u.Role)} · kart {u.BadgeCode}");
    });

    private bool HasSelection() => SelectedUser is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ResetPinAsync() => Run(async () =>
    {
        var u = await _api.UpdateUserAsync(SelectedUser!.UserId, new UserUpdateRequest(null, null, null, ResetPinText));
        ResetPinText = "";
        await LoadUsersAsync();
        return ("PIN değiştirildi", $"{u.Name} yeni PIN ile giriş yapar (kilit de açıldı).");
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ToggleActiveAsync() => Run(async () =>
    {
        var u = await _api.UpdateUserAsync(SelectedUser!.UserId, new UserUpdateRequest(null, null, !SelectedUser.Active, null));
        await LoadUsersAsync();
        return (u.Active ? "Etkinleştirildi" : "Pasif yapıldı", u.Active ? u.Name : $"{u.Name} artık giriş yapamaz; geçmiş kayıtlarda adı kalır.");
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ToggleRoleAsync() => Run(async () =>
    {
        var role = SelectedUser!.Role == UserRoles.Supervisor ? UserRoles.Warehouse : UserRoles.Supervisor;
        var u = await _api.UpdateUserAsync(SelectedUser.UserId, new UserUpdateRequest(null, role, null, null));
        await LoadUsersAsync();
        return ("Rol değişti", $"{u.Name}: {UserRoles.Display(u.Role)}");
    });

    private async Task Run(Func<Task<(string Title, string Text)>> action, bool warnOnly = false)
    {
        try
        {
            var (title, text) = await action();
            Show(warnOnly && title == "Yetkili değil" ? InfoBarSeverity.Warning : InfoBarSeverity.Success, title, text);
        }
        catch (SyncException ex)
        {
            Show(InfoBarSeverity.Error, "Yapılamadı", ex.Message);
        }
    }

    private void Show(InfoBarSeverity severity, string title, string text)
    {
        MessageSeverity = severity;
        MessageTitle = title;
        MessageText = text;
        MessageIsOpen = true;
    }
}
