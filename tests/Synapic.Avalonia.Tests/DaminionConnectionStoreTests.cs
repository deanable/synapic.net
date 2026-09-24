using Synapic.Avalonia.Models;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.ViewModels.Steps;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// Registry persistence of every Step 1 Daminion field (connection params +
/// scope/filter/limit settings). Tests run against an isolated HKCU key path
/// so they never touch the real Software\Synapic\Daminion key, and clear up
/// after themselves. DPAPI password round-trips within the same Windows user
/// (the production threat model).
/// Skipped on non-Windows (CI runs the suite on ubuntu-22.04; the store is a
/// no-op there by design).
/// </summary>
public class DaminionConnectionStoreTests : IDisposable
{
    private readonly string _keyPath =
        $@"Software\Synapic\Tests_{Guid.NewGuid():N}";

    private readonly DaminionConnectionStore _store;

    public DaminionConnectionStoreTests() => _store = new DaminionConnectionStore(_keyPath);

    public void Dispose() => _store.Clear();

    [Fact]
    public void Save_then_Load_round_trips_all_connection_fields()
    {
        if (!OperatingSystem.IsWindows()) return; // registry is Windows-only

        _store.Save(new DaminionConnectionParams(
            "http://damserver.local/daminion", "admin", "s3cret!",
            "all", "", "", "", "all", false, false, false, 100, 100, false));

        var loaded = _store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("http://damserver.local/daminion", loaded.ServerUrl);
        Assert.Equal("admin", loaded.Username);
        Assert.Equal("s3cret!", loaded.Password);
    }

    [Fact]
    public void Load_returns_null_when_nothing_saved()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Null(_store.Load());
    }

    [Fact]
    public void Password_is_not_stored_as_plain_text()
    {
        if (!OperatingSystem.IsWindows()) return;

        _store.Save(new DaminionConnectionParams(
            "http://damserver.local/daminion", "admin", "s3cret!",
            "all", "", "", "", "all", false, false, false, 100, 100, false));

        // Read the raw registry value: it must be a non-empty binary blob and
        // must not contain the plaintext anywhere.
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(_keyPath);
        Assert.NotNull(key);
        var blob = Assert.IsType<byte[]>(key.GetValue("PasswordEncrypted"));
        Assert.NotEmpty(blob);
        var asText = System.Text.Encoding.UTF8.GetString(blob);
        Assert.DoesNotContain("s3cret!", asText);
    }

    [Fact]
    public void Load_survives_a_corrupted_password_blob()
    {
        if (!OperatingSystem.IsWindows()) return;

        _store.Save(new DaminionConnectionParams(
            "http://damserver.local/daminion", "admin", "s3cret!",
            "all", "", "", "", "all", false, false, false, 100, 100, false));

        // Corrupt the ciphertext (simulates another-user/moved-profile data).
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(_keyPath, writable: true))
        {
            Assert.NotNull(key);
            key.SetValue("PasswordEncrypted", new byte[] { 1, 2, 3, 4 }, Microsoft.Win32.RegistryValueKind.Binary);
        }

        var loaded = _store.Load();

        // The secret is dropped but the rest of the connection survives.
        Assert.NotNull(loaded);
        Assert.Equal("http://damserver.local/daminion", loaded.ServerUrl);
        Assert.Equal("admin", loaded.Username);
        Assert.Equal("", loaded.Password);
    }

    [Fact]
    public void Empty_password_clears_the_stored_secret()
    {
        if (!OperatingSystem.IsWindows()) return;

        _store.Save(new DaminionConnectionParams(
            "http://damserver.local/daminion", "admin", "s3cret!",
            "all", "", "", "", "all", false, false, false, 100, 100, false));
        _store.Save(new DaminionConnectionParams(
            "http://damserver.local/daminion", "admin", "",
            "all", "", "", "", "all", false, false, false, 100, 100, false));

        var loaded = _store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("", loaded.Password);
        Assert.Equal("admin", loaded.Username);
    }

    [Fact]
    public void Clear_removes_everything()
    {
        if (!OperatingSystem.IsWindows()) return;

        _store.Save(new DaminionConnectionParams(
            "http://damserver.local/daminion", "admin", "s3cret!",
            "all", "", "", "", "all", false, false, false, 100, 100, false));
        _store.Clear();

        Assert.Null(_store.Load());
    }

    [Fact]
    public void Save_then_Load_round_trips_every_Step1_field()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Exercise the full form: non-default scope, search term, saved search +
        // collection ids, status filter, all three untagged flags, max items, a
        // mid-range resize scale, and the thumbnail override.
        _store.Save(new DaminionConnectionParams(
            ServerUrl: "http://damserver.local/daminion",
            Username: "admin",
            Password: "s3cret!",
            DaminionScope: "search",
            SearchTerm: "forest lake",
            SavedSearchId: "7",
            CollectionId: "",
            StatusFilter: "approved",
            UntaggedKeywords: true,
            UntaggedCategories: false,
            UntaggedDescription: true,
            MaxItems: 400,
            ResizeScale: 75,
            UseThumbnailOverride: true));

        var loaded = _store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("http://damserver.local/daminion", loaded.ServerUrl);
        Assert.Equal("admin", loaded.Username);
        Assert.Equal("s3cret!", loaded.Password);
        Assert.Equal("search", loaded.DaminionScope);
        Assert.Equal("forest lake", loaded.SearchTerm);
        Assert.Equal("7", loaded.SavedSearchId);
        Assert.Equal("", loaded.CollectionId);
        Assert.Equal("approved", loaded.StatusFilter);
        Assert.True(loaded.UntaggedKeywords);
        Assert.False(loaded.UntaggedCategories);
        Assert.True(loaded.UntaggedDescription);
        Assert.Equal(400, loaded.MaxItems);
        Assert.Equal(75, loaded.ResizeScale);
        Assert.True(loaded.UseThumbnailOverride);
    }

    [Fact]
    public void Load_defaults_missing_optional_fields_to_form_defaults()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Only the connection params saved — scope/filter/limit fields should
        // fall back to the form defaults (all / "" / all / 100 / 100 / false).
        _store.Save(new DaminionConnectionParams(
            "http://x", "u", "p",
            "all", "", "", "", "all", false, false, false, 100, 100, false));

        var loaded = _store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("all", loaded.DaminionScope);
        Assert.Equal("", loaded.SearchTerm);
        Assert.Equal("", loaded.SavedSearchId);
        Assert.Equal("", loaded.CollectionId);
        Assert.Equal("all", loaded.StatusFilter);
        Assert.False(loaded.UntaggedKeywords);
        Assert.False(loaded.UntaggedCategories);
        Assert.False(loaded.UntaggedDescription);
        Assert.Equal(100, loaded.MaxItems);
        Assert.Equal(100, loaded.ResizeScale);
        Assert.False(loaded.UseThumbnailOverride);
    }

    [Fact]
    public void Step1_hydrates_every_Step1_field_from_the_store()
    {
        if (!OperatingSystem.IsWindows()) return;

        _store.Save(new DaminionConnectionParams(
            ServerUrl: "http://damserver.local/daminion",
            Username: "admin",
            Password: "s3cret!",
            DaminionScope: "search",
            SearchTerm: "forest lake",
            SavedSearchId: "7",
            CollectionId: "",
            StatusFilter: "approved",
            UntaggedKeywords: true,
            UntaggedCategories: false,
            UntaggedDescription: true,
            MaxItems: 400,
            ResizeScale: 75,
            UseThumbnailOverride: true));

        var vm = new Step1DatasourceViewModel(new Session(), _store);

        Assert.Equal("http://damserver.local/daminion", vm.DaminionUrl);
        Assert.Equal("admin", vm.DaminionUser);
        Assert.Equal("s3cret!", vm.DaminionPass);
        Assert.Equal(1, vm.ScopeIndex); // "search"
        Assert.Equal("forest lake", vm.SearchTerm);
        Assert.Equal("7", vm.SavedSearchId);
        Assert.Equal("", vm.CollectionId);
        Assert.Equal(1, vm.StatusFilterIndex); // "approved"
        Assert.True(vm.UntaggedKeywords);
        Assert.False(vm.UntaggedCategories);
        Assert.True(vm.UntaggedDescription);
        Assert.Equal(400, vm.MaxItems);
        Assert.Equal(1, vm.ResizeScaleIndex); // 75%
        Assert.True(vm.UseThumbnailOverride);
    }

    [Fact]
    public void Step1_preserves_non_default_scope_and_filters()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Hydration passes every field through, including non-default ScopeIndex /
        // StatusFilterIndex values (they round-trip via the string form).
        _store.Save(new DaminionConnectionParams(
            ServerUrl: "http://x", Username: "u", Password: "p",
            DaminionScope: "saved_search", SearchTerm: "", SavedSearchId: "3",
            CollectionId: "", StatusFilter: "unassigned",
            UntaggedKeywords: true, UntaggedCategories: true,
            UntaggedDescription: false, MaxItems: 50, ResizeScale: 25,
            UseThumbnailOverride: false));

        var vm = new Step1DatasourceViewModel(new Session(), _store);

        Assert.Equal(3, vm.ScopeIndex); // "saved_search" (index 3)
        Assert.Equal("unassigned", vm.StatusFilter);
        Assert.Equal(3, vm.StatusFilterIndex); // "unassigned" (index 3)
        Assert.True(vm.UntaggedKeywords);
        Assert.True(vm.UntaggedCategories);
        Assert.False(vm.UntaggedDescription);
        Assert.Equal(50, vm.MaxItems);
        Assert.Equal(3, vm.ResizeScaleIndex); // 25% (index 3)
        Assert.False(vm.UseThumbnailOverride);
    }

    [Fact]
    public void Step1_without_store_leaves_connection_fields_empty()
    {
        var vm = new Step1DatasourceViewModel(new Session());

        Assert.Equal("", vm.DaminionUrl);
        Assert.Equal("", vm.DaminionUser);
        Assert.Equal("", vm.DaminionPass);
        Assert.Equal(0, vm.ScopeIndex);
        Assert.Equal("", vm.SearchTerm);
        Assert.Equal("", vm.SavedSearchId);
        Assert.Equal("", vm.CollectionId);
        Assert.Equal(0, vm.StatusFilterIndex);
        Assert.False(vm.UntaggedKeywords);
        Assert.False(vm.UntaggedCategories);
        Assert.False(vm.UntaggedDescription);
        Assert.Equal(100, vm.MaxItems);
        Assert.Equal(0, vm.ResizeScaleIndex);
        Assert.False(vm.UseThumbnailOverride);
    }
}
