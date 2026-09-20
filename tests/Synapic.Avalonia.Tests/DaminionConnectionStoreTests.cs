using Synapic.Avalonia.Models;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.ViewModels.Steps;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// Registry persistence of the Daminion connection parameters (Step 1
/// convenience). Tests run against an isolated HKCU key path so they never
/// touch the real Software\Synapic\Daminion key, and clear up after
/// themselves. On the DPAPI password: each test round-trips within the same
/// Windows user, which is exactly the production threat model.
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
    public void Save_then_Load_round_trips_all_fields()
    {
        if (!OperatingSystem.IsWindows()) return; // registry is Windows-only

        _store.Save(new DaminionConnectionParams(
            "http://damserver.local/daminion", "admin", "s3cret!", "MyCatalog"));

        var loaded = _store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("http://damserver.local/daminion", loaded.ServerUrl);
        Assert.Equal("admin", loaded.Username);
        Assert.Equal("s3cret!", loaded.Password);
        Assert.Equal("MyCatalog", loaded.CatalogId);
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
            "http://damserver.local/daminion", "admin", "s3cret!", ""));

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
            "http://damserver.local/daminion", "admin", "s3cret!", "Cat"));

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
        Assert.Equal("Cat", loaded.CatalogId);
        Assert.Equal("", loaded.Password);
    }

    [Fact]
    public void Empty_password_clears_the_stored_secret()
    {
        if (!OperatingSystem.IsWindows()) return;

        _store.Save(new DaminionConnectionParams(
            "http://damserver.local/daminion", "admin", "s3cret!", ""));
        _store.Save(new DaminionConnectionParams(
            "http://damserver.local/daminion", "admin", "", ""));

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
            "http://damserver.local/daminion", "admin", "s3cret!", ""));
        _store.Clear();

        Assert.Null(_store.Load());
    }

    [Fact]
    public void Step1_hydrates_connection_fields_from_the_store()
    {
        if (!OperatingSystem.IsWindows()) return;

        _store.Save(new DaminionConnectionParams(
            "http://damserver.local/daminion", "admin", "s3cret!", "Cat42"));

        var vm = new Step1DatasourceViewModel(new Session(), _store);

        Assert.Equal("http://damserver.local/daminion", vm.DaminionUrl);
        Assert.Equal("admin", vm.DaminionUser);
        Assert.Equal("s3cret!", vm.DaminionPass);
        Assert.Equal("Cat42", vm.DaminionCatalogId);
    }

    [Fact]
    public void Step1_without_store_leaves_fields_empty()
    {
        var vm = new Step1DatasourceViewModel(new Session());

        Assert.Equal("", vm.DaminionUrl);
        Assert.Equal("", vm.DaminionUser);
        Assert.Equal("", vm.DaminionPass);
    }
}
